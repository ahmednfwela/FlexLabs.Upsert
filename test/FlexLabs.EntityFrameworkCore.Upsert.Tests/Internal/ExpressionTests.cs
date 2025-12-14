using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using FlexLabs.EntityFrameworkCore.Upsert;
using FlexLabs.EntityFrameworkCore.Upsert.Internal.Expressions;
using FlexLabs.EntityFrameworkCore.Upsert.Runners;
using FluentAssertions;
using Xunit;


namespace FlexLabs.EntityFrameworkCore.Upsert.Tests.Internal;

public partial class ExpressionTests(ITestOutputHelper output) {
    private readonly ExpressionParser<TestEntity> _parser = new(new TestRelationalTable(), new RunnerQueryOptions());
    private readonly ExpressionParser<TestEntity> _parserWithCompiler = new(new TestRelationalTable(), new RunnerQueryOptions { UseExpressionCompiler = true });

    #region helper

    private PropertyMapping[] Parse(Action<SetterBuilder> configure, bool useExpressionCompiler = false)
    {
        var builder = new SetterBuilder();
        configure(builder);

        var result = useExpressionCompiler
            ? _parserWithCompiler.ParseUpdateSetters(builder.Setters)
            : _parser.ParseUpdateSetters(builder.Setters);

        Print(result, builder.Setters);
        return result;
    }

    private void Print(IEnumerable<PropertyMapping> mappings, IReadOnlyList<UpsertSetter> setters)
    {
        output.WriteLine("Setters:");
        foreach (var setter in setters)
        {
            output.WriteLine($"  {setter.Member.Name} = {setter.ValueExpression}");
        }

        output.WriteLine("\nResult:");
        output.WriteLine("new TestEntity {");

        foreach (var mapping in mappings) {
            output.WriteLine($"  {mapping.Property.ColumnName} = {Expand(mapping.Value)},");
        }

        output.WriteLine("}");
        return;

        object Expand(IKnownValue value)
        {
            return value switch {
                ConstantValue x => x.Value,
                PropertyValue x => $"{(x.IsLeftParameter ? "a" : "b")}{x.Column.Path}.{x.Column.Name}",
                KnownExpression x => x.ExpressionType switch {
                    ExpressionType.Conditional => $"{Expand(x.Value3)} ? {Expand(x.Value1)} : {Expand(x.Value2)}",
                    ExpressionType.LessThan => $"{Expand(x.Value1)} < {Expand(x.Value2)}",
                    ExpressionType.NotEqual => $"{Expand(x.Value1)} != {Expand(x.Value2)}",
                    ExpressionType.Add => $"{Expand(x.Value1)} + {Expand(x.Value2)}",
                    ExpressionType.Subtract => $"{Expand(x.Value1)} - {Expand(x.Value2)}",
                    ExpressionType.Multiply => $"{Expand(x.Value1)} * {Expand(x.Value2)}",
                    ExpressionType.Divide => $"{Expand(x.Value1)} / {Expand(x.Value2)}",
                    ExpressionType.AndAlso => $"{Expand(x.Value1)} && {Expand(x.Value2)}",
                    ExpressionType.OrElse => $"{Expand(x.Value1)} || {Expand(x.Value2)}",
                    ExpressionType.And => $"{Expand(x.Value1)} & {Expand(x.Value2)}",
                    ExpressionType.Or => $"{Expand(x.Value1)} | {Expand(x.Value2)}",
                    ExpressionType.Modulo => $"{Expand(x.Value1)} % {Expand(x.Value2)}",
                    _ => throw new ArgumentOutOfRangeException(nameof(x), x.ExpressionType, null)
                },
                _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
            };
        }
    }

    private sealed class SetterBuilder
    {
        private readonly ParameterExpression _existing = Expression.Parameter(typeof(TestEntity), "a");
        private readonly ParameterExpression _incoming = Expression.Parameter(typeof(TestEntity), "b");

        public List<UpsertSetter> Setters { get; } = new();

        public SetterBuilder Set<TProperty>(
            Expression<Func<TestEntity, TProperty>> propertyExpression,
            Expression<Func<TestEntity, TProperty>> valueExpression)
        {
            ArgumentNullException.ThrowIfNull(propertyExpression);
            ArgumentNullException.ThrowIfNull(valueExpression);

            var member = GetMember(propertyExpression.Body);
            var normalizedValue = NormalizeValueLambda(valueExpression);
            Setters.Add(new UpsertSetter(member, normalizedValue));
            return this;
        }

        public SetterBuilder Set<TProperty>(
            Expression<Func<TestEntity, TProperty>> propertyExpression,
            Expression<Func<TestEntity, TestEntity, TProperty>> valueExpression)
        {
            ArgumentNullException.ThrowIfNull(propertyExpression);
            ArgumentNullException.ThrowIfNull(valueExpression);

            var member = GetMember(propertyExpression.Body);
            var normalizedValue = NormalizeValueLambda(valueExpression);
            Setters.Add(new UpsertSetter(member, normalizedValue));
            return this;
        }

        private LambdaExpression NormalizeValueLambda<TProperty>(Expression<Func<TestEntity, TProperty>> valueExpression)
        {
            var body = new ParameterReplaceVisitor(valueExpression.Parameters[0], _existing)
                .Visit(valueExpression.Body);

            var normalized = Expression.Lambda(body!, _existing, _incoming);
            return ExpressionNormalizer.NormalizeLambda(normalized);
        }

        private LambdaExpression NormalizeValueLambda<TProperty>(Expression<Func<TestEntity, TestEntity, TProperty>> valueExpression)
        {
            var body = new ParameterReplaceVisitor(valueExpression.Parameters[0], _existing)
                .Visit(valueExpression.Body);

            body = new ParameterReplaceVisitor(valueExpression.Parameters[1], _incoming)
                .Visit(body!);

            var normalized = Expression.Lambda(body!, _existing, _incoming);
            return ExpressionNormalizer.NormalizeLambda(normalized);
        }

        private static MemberInfo GetMember(Expression expression)
        {
            expression = StripConvert(expression);

            if (expression is not MemberExpression memberExpression)
                throw new InvalidOperationException("Set() requires a simple member access like e => e.SomeProperty.");

            if (memberExpression.Expression is not ParameterExpression)
                throw new InvalidOperationException("Set() requires a direct property access on the entity parameter.");

            return memberExpression.Member;
        }

        private static Expression StripConvert(Expression expression)
        {
            while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            {
                expression = unary.Operand;
            }

            return expression;
        }

        private sealed class ParameterReplaceVisitor(ParameterExpression from, Expression to) : ExpressionVisitor
        {
            protected override Expression VisitParameter(ParameterExpression node)
                => node == from ? to : base.VisitParameter(node);
        }
    }

    #endregion


    [Fact]
    public void Supports_Constant()
    {
        var result = Parse(set => set.Set(e => e.Num1, _ => 1));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<ConstantValue>()
            .Which.Value.Should().Be(1)
        );
    }

    [Fact]
    public void Supports_Field()
    {
        var value = 2;
        var result = Parse(set => set.Set(e => e.Num1, _ => value));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<ConstantValue>()
            .Which.Value.Should().Be(value)
        );
    }

    [Fact]
    public void Supports_FieldAndProperty()
    {
        var value = new TestEntity { Num1 = 3 };
        var result = Parse(set => set.Set(e => e.Num1, _ => value.Num1));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<ConstantValue>()
            .Which.Value.Should().Be(value.Num1)
        );
    }

    [Fact]
    public void Supports_Method()
    {
        var value = "hello_world ";
        var result = Parse(set => set.Set(e => e.Text1, _ => value.Trim()));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Text1")
            .WithValueOfType<ConstantValue>()
            .Which.Value.Should().Be(value.Trim())
        );
    }

    [Fact]
    public void Supports_StaticMethod()
    {
        var value1 = "hello";
        var value2 = "world";
        var result = Parse(set => set.Set(e => e.Text1, _ => string.Join(", ", new string[] { value1, value2 })));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Text1")
            .WithValueOfType<ConstantValue>()
            .Which.Value.Should().Be(value1 + ", " + value2)
        );
    }

    [Fact]
    public void Supports_ValueIncrement()
    {
        var result = Parse(set => set.Set(e => e.Num1, a => a.Num1 + 1));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<KnownExpression>()
            .Which.Should().BeKnownExpression(ExpressionType.Add)
            .HavePropertyValue(e => e.Value1, "Num1", true)
            .HaveConstantValue(e => e.Value2, 1)
        );
    }

    [Fact]
    public void Supports_ValueIncrement_Reverse()
    {
        var result = Parse(set => set.Set(e => e.Num1, a => 1 + a.Num1));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<KnownExpression>()
            .Which.Should().BeKnownExpression(ExpressionType.Add)
            .HaveConstantValue(e => e.Value1, 1)
            .HavePropertyValue(e => e.Value2, "Num1", true)
        );
    }

    [Fact]
    public void Supports_ValueSubtract()
    {
        var result = Parse(set => set.Set(e => e.Num1, a => a.Num1 - 2));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<KnownExpression>()
            .Which.Should().BeKnownExpression(ExpressionType.Subtract)
            .HavePropertyValue(e => e.Value1, "Num1", true)
            .HaveConstantValue(e => e.Value2, 2)
        );
    }

    [Fact]
    public void Supports_ValueMultiply()
    {
        var result = Parse(set => set.Set(e => e.Num1, a => a.Num1 * 3));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<KnownExpression>()
            .Which.Should().BeKnownExpression(ExpressionType.Multiply)
            .HavePropertyValue(e => e.Value1, "Num1", true)
            .HaveConstantValue(e => e.Value2, 3)
        );
    }

    [Fact]
    public void Supports_ValueDivide()
    {
        var result = Parse(set => set.Set(e => e.Num1, a => a.Num1 / 4));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<KnownExpression>()
            .Which.Should().BeKnownExpression(ExpressionType.Divide)
            .HavePropertyValue(e => e.Value1, "Num1", true)
            .HaveConstantValue(e => e.Value2, 4)
        );
    }

    [Fact]
    public void Supports_ValueModulo()
    {
        var result = Parse(set => set.Set(e => e.Num1, a => a.Num1 % 4));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<KnownExpression>()
            .Which.Should().BeKnownExpression(ExpressionType.Modulo)
            .HavePropertyValue(e => e.Value1, "Num1", true)
            .HaveConstantValue(e => e.Value2, 4)
        );
    }

    [Fact]
    public void Supports_ValueBitwiseOr()
    {
        var result = Parse(set => set.Set(e => e.Num1, a => a.Num1 | 1));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<KnownExpression>()
            .Which.Should().BeKnownExpression(ExpressionType.Or)
            .HavePropertyValue(e => e.Value1, "Num1", true)
            .HaveConstantValue(e => e.Value2, 1)
        );
    }

    [Fact]
    public void Supports_ValueBitwiseAnd()
    {
        var result = Parse(set => set.Set(e => e.Num1, a => a.Num1 & 1));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<KnownExpression>()
            .Which.Should().BeKnownExpression(ExpressionType.And)
            .HavePropertyValue(e => e.Value1, "Num1", true)
            .HaveConstantValue(e => e.Value2, 1)
        );
    }

    [Fact]
    public void Supports_Property()
    {
        var result = Parse(set => set.Set(e => e.Num1, a => a.Num1));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<PropertyValue>()
            .Which.Should().BePropertyValue("Num1", true)
        );
    }

    [Fact]
    public void Supports_PropertyOther()
    {
        var result = Parse(set => set.Set(e => e.Num1, a => a.Num2));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<PropertyValue>()
            .Which.Should().BePropertyValue("Num2", true)
        );
    }

    [Fact]
    public void Supports_Property_WithSource()
    {
        var result = Parse(set => set.Set(e => e.Num1, e1 => e1.Num1));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<PropertyValue>()
            .Which.Should().BePropertyValue("Num1", true)
        );
    }

    [Fact]
    public void Supports_Property_FromSource()
    {
        var result = Parse(set => set.Set(e => e.Num1, (e1, e2) => e2.Num1));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<PropertyValue>()
            .Which.Should().BePropertyValue("Num1", false)
        );
    }

    [Fact]
    public void Supports_DateTime_Now()
    {
        var result = Parse(set => set.Set(e => e.Updated, _ => DateTime.Now));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Updated")
            .WithValueOfType<ConstantValue>()
            .Which.Value.Should().BeOfType<DateTime>().Subject
            .Should().BeBefore(DateTime.Now.AddMinutes(1))
            .And.BeAfter(DateTime.Now.AddMinutes(-1))
        );
    }

    [Fact]
    public void Supports_Nullable_Assign()
    {
        int value = 5;

        var result = Parse(set => set.Set(e => e.NumNullable1, _ => value));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("NumNullable1")
            .WithValueOfType<ConstantValue>()
            .Which.Value.Should().BeOfType<int>().And.Be(value)
        );
    }

    [Fact]
    public void Supports_Nullable_Cast()
    {
        int? value = 5;

        var result = Parse(set => set.Set(e => e.Num1, _ => (int)value));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<ConstantValue>()
            .Which.Value.Should().BeOfType<int>().And.Be(value)
        );
    }

    [Fact]
    public void Supports_Nullable_Coalesce()
    {
        int? value = 5;

        var result = Parse(set => set.Set(e => e.Num1, _ => value ?? 0));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<ConstantValue>()
            .Which.Value.Should().BeOfType<int>().And.Be(value)
        );
    }

    [Fact]
    public void Supports_Nullable_GetValueOrDefault()
    {
        int? value = 5;

        var result = Parse(set => set.Set(e => e.Num1, _ => value.GetValueOrDefault()));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<ConstantValue>()
            .Which.Value.Should().BeOfType<int>().And.Be(value)
        );
    }

    [Fact]
    public void Supports_UnsupportedExpression_With_Compile()
    {
        var input = 5;

        var action = () => Parse(set => set.Set(e => e.Num1, _ => input << 4));
        action.Should().Throw<UnsupportedExpressionException>();

        var result = Parse(set => set.Set(e => e.Num1, _ => input << 4), useExpressionCompiler: true);

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<ConstantValue>()
            .Which.Value.Should().BeOfType<int>().And.Be(input << 4)
        );
    }

    [Fact]
    public void CompoundExpression_Sum()
    {
        var result = Parse(set => set.Set(e => e.Text1, (e1, e2) => e1.Text1 + "." + e2.Text2));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Text1")
            .WithValueOfType<KnownExpression>()
            .Which.Should().BeKnownExpression(ExpressionType.Add)
            .HaveKnownExpression(e => e.Value1, ExpressionType.Add, ke => ke
                .HavePropertyValue(e => e.Value1, "Text1", true)
                .HaveConstantValue(e => e.Value2, "."))
            .HavePropertyValue(e => e.Value2, "Text2", false)
        );
    }

    [Fact]
    public void CompoundExpression_Sum_Grouped1()
    {
        var result = Parse(set => set.Set(e => e.Text1, (e1, e2) => (e1.Text1 + ".") + e2.Text2));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Text1")
            .WithValueOfType<KnownExpression>()
            .Which.Should().BeKnownExpression(ExpressionType.Add)
            .HaveKnownExpression(e => e.Value1, ExpressionType.Add, ke => ke
                .HavePropertyValue(e => e.Value1, "Text1", true)
                .HaveConstantValue(e => e.Value2, "."))
            .HavePropertyValue(e => e.Value2, "Text2", false)
        );
    }

    [Fact]
    public void CompoundExpression_Sum_Grouped2()
    {
        var result = Parse(set => set.Set(e => e.Text1, (e1, e2) => e1.Text1 + ("." + e2.Text2)));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Text1")
            .WithValueOfType<KnownExpression>()
            .Which.Should().BeKnownExpression(ExpressionType.Add)
            .HavePropertyValue(e => e.Value1, "Text1", true)
            .HaveKnownExpression(e => e.Value2, ExpressionType.Add, ke => ke
                .HaveConstantValue(e => e.Value1, ".")
                .HavePropertyValue(e => e.Value2, "Text2", false))
        );
    }

    [Fact]
    public void CompoundExpression_Multiply()
    {
        var result = Parse(set => set.Set(e => e.Num1, (e1, e2) => e1.Num1 + 7 * e2.Num2));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<KnownExpression>()
            .Which.Should().BeKnownExpression(ExpressionType.Add)
            .HavePropertyValue(e => e.Value1, "Num1", true)
            .HaveKnownExpression(e => e.Value2, ExpressionType.Multiply, ke => ke
                .HaveConstantValue(e => e.Value1, 7)
                .HavePropertyValue(e => e.Value2, "Num2", false))
        );
    }

    [Fact]
    public void CompoundExpression_Multiply_Grouped1()
    {
        var result = Parse(set => set.Set(e => e.Num1, (e1, e2) => (e1.Num1 + 7) * e2.Num2));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<KnownExpression>()
            .Which.Should().BeKnownExpression(ExpressionType.Multiply)
            .HaveKnownExpression(e => e.Value1, ExpressionType.Add, ke => ke
                .HavePropertyValue(e => e.Value1, "Num1", true)
                .HaveConstantValue(e => e.Value2, 7))
            .HavePropertyValue(e => e.Value2, "Num2", false)
        );
    }

    [Fact]
    public void CompoundExpression_Multiply_Grouped2()
    {
        var result = Parse(set => set.Set(e => e.Num1, (e1, e2) => e1.Num1 + (7 * e2.Num2)));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<KnownExpression>()
            .Which.Should().BeKnownExpression(ExpressionType.Add)
            .HavePropertyValue(e => e.Value1, "Num1", true)
            .HaveKnownExpression(e => e.Value2, ExpressionType.Multiply, ke => ke
                .HaveConstantValue(e => e.Value1, 7)
                .HavePropertyValue(e => e.Value2, "Num2", false))
        );
    }

    [Fact]
    public void CompoundExpression_Conditional()
    {
        var result = Parse(set => set.Set(e => e.Num1, a => a.Num1 + 7 < 0 ? 0 : a.Num1 + 7));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<KnownExpression>()
            .Which.Should().BeKnownExpression(ExpressionType.Conditional)
            .HaveConstantValue(e => e.Value1, 0)
            .HaveKnownExpression(e => e.Value2, ExpressionType.Add, ke => ke
                .HavePropertyValue(e => e.Value1, "Num1", true)
                .HaveConstantValue(e => e.Value2, 7))
            .HaveKnownExpression(e => e.Value3, ExpressionType.LessThan, ke => ke
                .HaveKnownExpression(e => e.Value1, ExpressionType.Add, ke2 => ke2
                    .HavePropertyValue(e => e.Value1, "Num1", true)
                    .HaveConstantValue(e => e.Value2, 7))
                .HaveConstantValue(e => e.Value2, 0))
        );
    }

    [Fact]
    public void CompoundExpression_Conditional_NotEqual()
    {
        var result = Parse(set => set.Set(e => e.Num1, e1 => e1.Num1 != 4 ? 0 : 1));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<KnownExpression>()
            .Which.Should().BeKnownExpression(ExpressionType.Conditional)
            .HaveConstantValue(e => e.Value1, 0)
            .HaveConstantValue(e => e.Value2, 1)
            .HaveKnownExpression(e => e.Value3, ExpressionType.NotEqual, ke => ke
                .HavePropertyValue(e => e.Value1, "Num1", true)
                .HaveConstantValue(e => e.Value2, 4))
        );
    }

    [Fact]
    public void CompoundExpression_Conditional_NotNull()
    {
        var result = Parse(set => set.Set(e => e.Num1, e1 => e1.Text1 != null ? 0 : 1));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<KnownExpression>()
            .Which.Should().BeKnownExpression(ExpressionType.Conditional)
            .HaveConstantValue(e => e.Value1, 0)
            .HaveConstantValue(e => e.Value2, 1)
            .HaveKnownExpression(e => e.Value3, ExpressionType.NotEqual, ke => ke
                .HavePropertyValue(e => e.Value1, "Text1", true)
                .HaveConstantValue(e => e.Value2, null))
        );
    }

    [Fact]
    public void Condition_AndAlso()
    {
        var result = Parse(set => set.Set(e => e.Boolean, (e1, e2) => e1.Num1 != e2.Num1 && e1.Text1 != e2.Text1));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Boolean")
            .WithValueOfType<KnownExpression>()
            .Which.Should().BeKnownExpression(ExpressionType.AndAlso)
            .HaveKnownExpression(e => e.Value1, ExpressionType.NotEqual, ke => ke
                .HavePropertyValue(e => e.Value1, "Num1", true)
                .HavePropertyValue(e => e.Value2, "Num1", false))
            .HaveKnownExpression(e => e.Value2, ExpressionType.NotEqual, ke => ke
                .HavePropertyValue(e => e.Value1, "Text1", true)
                .HavePropertyValue(e => e.Value2, "Text1", false))
        );
    }

    [Fact]
    public void Condition_ElseIf()
    {
        var result = Parse(set => set.Set(e => e.Boolean, (e1, e2) => e1.Num1 != e2.Num1 || e1.Text1 != e2.Text1));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Boolean")
            .WithValueOfType<KnownExpression>()
            .Which.Should().BeKnownExpression(ExpressionType.OrElse)
            .HaveKnownExpression(e => e.Value1, ExpressionType.NotEqual, ke => ke
                .HavePropertyValue(e => e.Value1, "Num1", true)
                .HavePropertyValue(e => e.Value2, "Num1", false))
            .HaveKnownExpression(e => e.Value2, ExpressionType.NotEqual, ke => ke
                .HavePropertyValue(e => e.Value1, "Text1", true)
                .HavePropertyValue(e => e.Value2, "Text1", false))
        );
    }

    [Fact]
    public void UpdateCondition_CanHandleImplicitNumericConversion()
    {
        // Internally these seem to be wrapped in an implicit Convert expression. Ensure this is handled correctly.
        var result = _parser.ParseUpdateConditionExpression((a, e) => a.Short1 == e.Short1);

        result.Should().BeKnownExpression(ExpressionType.Equal)
            .HavePropertyValue(e => e.Value1, "Short1", true)
            .HavePropertyValue(e => e.Value2, "Short1", false);
    }

    [Fact]
    public void UpdateCondition_CanHandleImplicitTypeMapping()
    {
        var result = Parse(set => set.Set(e => e.Num1, (e1, e2) => e2.Short1));

        result[0].Should().BePropertyMapping(_ => _
            .WithColumn("Num1")
            .WithValueOfType<PropertyValue>()
            .Which.Should().BePropertyValue("Short1", false)
        );
    }
}
