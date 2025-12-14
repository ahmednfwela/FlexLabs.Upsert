using System;
using System.Linq.Expressions;

namespace FlexLabs.EntityFrameworkCore.Upsert.Internal.Expressions;

internal static class ExpressionNormalizer
{
    public static Expression<TDelegate> Normalize<TDelegate>(Expression<TDelegate> lambda)
        => (Expression<TDelegate>)NormalizeLambda(lambda);

    public static LambdaExpression NormalizeLambda(LambdaExpression lambda)
    {
        ArgumentNullException.ThrowIfNull(lambda);

        var body = new Visitor().Visit(lambda.Body)!;
        return Expression.Lambda(lambda.Type, body, lambda.Parameters);
    }

    private sealed class Visitor : ExpressionVisitor
    {
        protected override Expression VisitUnary(UnaryExpression node)
        {
            if (node.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
            {
                // Strip boxing to object: Convert(valueType, object)
                if (node.Type == typeof(object) && node.Operand.Type.IsValueType)
                {
                    return Visit(node.Operand);
                }

                // Strip identity converts
                if (node.Type == node.Operand.Type)
                {
                    return Visit(node.Operand);
                }
            }

            return base.VisitUnary(node);
        }
    }
}
