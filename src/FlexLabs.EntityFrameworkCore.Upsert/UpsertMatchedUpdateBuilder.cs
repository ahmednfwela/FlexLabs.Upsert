using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace FlexLabs.EntityFrameworkCore.Upsert;

/// <summary>
/// Builder used to configure column updates for <see cref="UpsertCommandBuilder{TEntity}.WhenMatched(System.Action{FlexLabs.EntityFrameworkCore.Upsert.UpsertMatchedUpdateBuilder{TEntity}})"/>.
/// </summary>
/// <typeparam name="TEntity">The entity type being upserted.</typeparam>
public sealed class UpsertMatchedUpdateBuilder<TEntity>
    where TEntity : class
{
    private readonly List<UpsertSetter> _setters = new();
    private readonly ParameterExpression _existing;
    private readonly ParameterExpression _incoming;

    internal IReadOnlyList<UpsertSetter> Setters => _setters;

    /// <summary>
    /// Sets a property to a value computed from the existing (database) row.
    /// </summary>
    public UpsertMatchedUpdateBuilder<TEntity> SetProperty<TProperty>(
        Expression<Func<TEntity, TProperty>> propertyExpression,
        Expression<Func<TEntity, TProperty>> valueExpression
    )
    {
        ArgumentNullException.ThrowIfNull(propertyExpression);
        ArgumentNullException.ThrowIfNull(valueExpression);

        var member = GetMember(propertyExpression.Body);
        var normalizedValue = NormalizeValueLambda(valueExpression);
        AddSetter(member, normalizedValue);

        return this;
    }

    /// <summary>
    /// Sets a property to a value computed from the existing (database) row and the incoming (insert) row.
    /// </summary>
    public UpsertMatchedUpdateBuilder<TEntity> SetProperty<TProperty>(
        Expression<Func<TEntity, TProperty>> propertyExpression,
        Expression<Func<TEntity, TEntity, TProperty>> valueExpression
    )
    {
        ArgumentNullException.ThrowIfNull(propertyExpression);
        ArgumentNullException.ThrowIfNull(valueExpression);

        var member = GetMember(propertyExpression.Body);
        var normalizedValue = NormalizeValueLambda(valueExpression);
        AddSetter(member, normalizedValue);

        return this;
    }

    /// <summary>
    /// Creates a new instance of the builder.
    /// </summary>
    public UpsertMatchedUpdateBuilder()
    {
        _existing = Expression.Parameter(typeof(TEntity), "existing");
        _incoming = Expression.Parameter(typeof(TEntity), "incoming");
    }

    private void AddSetter(MemberInfo member, LambdaExpression normalizedValue)
    {
        if (_setters.Any(s => s.Member == member))
        {
            throw new InvalidOperationException(
                Resources.FormatPropertyWasAlreadyConfiguredViaSetProperty(member.Name)
            );
        }

        _setters.Add(new UpsertSetter(member, normalizedValue));
    }

    private LambdaExpression NormalizeValueLambda<TProperty>(
        Expression<Func<TEntity, TProperty>> valueExpression
    )
    {
        var body = new ParameterReplaceVisitor(valueExpression.Parameters[0], _existing).Visit(
            valueExpression.Body
        );
        var normalized = Expression.Lambda(body!, _existing, _incoming);
        return FlexLabs.EntityFrameworkCore.Upsert.Internal.Expressions.ExpressionNormalizer.NormalizeLambda(
            normalized
        );
    }

    private LambdaExpression NormalizeValueLambda<TProperty>(
        Expression<Func<TEntity, TEntity, TProperty>> valueExpression
    )
    {
        var body = new ParameterReplaceVisitor(valueExpression.Parameters[0], _existing).Visit(
            valueExpression.Body
        );

        body = new ParameterReplaceVisitor(valueExpression.Parameters[1], _incoming).Visit(body!);

        var normalized = Expression.Lambda(body!, _existing, _incoming);
        return FlexLabs.EntityFrameworkCore.Upsert.Internal.Expressions.ExpressionNormalizer.NormalizeLambda(
            normalized
        );
    }

    private static MemberInfo GetMember(Expression expression)
    {
        expression = StripConvert(expression);

        if (expression is not MemberExpression memberExpression)
            throw new InvalidOperationException(Resources.SetPropertyRequiresSimpleMemberAccess);

        if (memberExpression.Expression is not ParameterExpression)
            throw new InvalidOperationException(
                Resources.SetPropertyRequiresDirectPropertyAccessOnEntityParameter
            );

        return memberExpression.Member;
    }

    private static Expression StripConvert(Expression expression)
    {
        while (
            expression
                is UnaryExpression
                {
                    NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked
                } unary
        )
        {
            expression = unary.Operand;
        }

        return expression;
    }

    private static Type GetMemberType(MemberInfo member)
    {
        return member switch
        {
            PropertyInfo p => p.PropertyType,
            FieldInfo f => f.FieldType,
            _
                => throw new InvalidOperationException(
                    Resources.FormatUnsupportedMemberType(member.MemberType)
                )
        };
    }

    private sealed class ParameterReplaceVisitor(ParameterExpression from, Expression to)
        : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == from ? to : base.VisitParameter(node);
    }
}
