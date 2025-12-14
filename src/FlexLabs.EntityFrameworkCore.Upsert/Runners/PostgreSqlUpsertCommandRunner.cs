using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using FlexLabs.EntityFrameworkCore.Upsert.Internal.Expressions;

namespace FlexLabs.EntityFrameworkCore.Upsert.Runners
{
    /// <summary>
    /// Upsert command runner for the Npgsql.EntityFrameworkCore.PostgreSQL provider
    /// </summary>
    public class PostgreSqlUpsertCommandRunner : RelationalUpsertCommandRunner
    {
        /// <inheritdoc/>
        public override bool Supports(string providerName) => providerName == "Npgsql.EntityFrameworkCore.PostgreSQL";
        /// <inheritdoc/>
        protected override string EscapeName(string name) => "\"" + name + "\"";
        /// <inheritdoc/>
        protected override string? SourcePrefix => "EXCLUDED.";
        /// <inheritdoc/>
        protected override string? TargetPrefix => "\"T\".";
        /// <inheritdoc/>
        protected override int? MaxQueryParams => 32767;

        /// <inheritdoc/>
        public override string GenerateCommand(
            string tableName,
            ICollection<ICollection<(string ColumnName, ConstantValue Value, string? DefaultSql, bool AllowInserts)>> entities,
            ICollection<(string ColumnName, bool IsNullable)> joinColumns,
            ICollection<(string ColumnName, IKnownValue Value)>? updateExpressions,
            KnownExpression? updateCondition,
            bool returnResult = false)
        {
            var result = new StringBuilder();
            result.Append(CultureInfo.InvariantCulture, $"INSERT INTO {tableName} AS \"T\" (");
            result.Append(string.Join(", ", entities.First().Select(e => EscapeName(e.ColumnName))));
            result.Append(") VALUES (");
            result.Append(string.Join("), (", entities.Select(ec => string.Join(", ", ec.Select(e => e.DefaultSql ?? Parameter(e.Value.ArgumentIndex))))));
            result.Append(") ON CONFLICT (");
            result.Append(string.Join(", ", joinColumns.Select(c => EscapeName(c.ColumnName))));
            result.Append(") DO ");
            if (updateExpressions != null)
            {
                result.Append("UPDATE SET ");
                if (updateCondition != null && returnResult)
                {
                    var updateConditionSql = ExpandExpression(updateCondition);
                    // If we use a WHERE clause here, Postgres returns 0 rows when the condition is false.
                    // For RunAndReturn, we want the matched row returned even when no update occurs.
                    // Embed the condition into the SET expressions so values remain unchanged when false.
                    result.Append(string.Join(", ", updateExpressions.Select(e =>
                        $"{EscapeName(e.ColumnName)} = CASE WHEN {updateConditionSql} THEN {ExpandValue(e.Value)} ELSE {TargetPrefix}{EscapeName(e.ColumnName)} END")));
                }
                else
                {
                    result.Append(string.Join(", ", updateExpressions.Select(e => $"{EscapeName(e.ColumnName)} = {ExpandValue(e.Value)}")));
                    if (updateCondition != null)
                        result.Append(CultureInfo.InvariantCulture, $" WHERE {ExpandExpression(updateCondition)}");
                }
            }
            else
            {
                result.Append("NOTHING");
            }

            if (returnResult)
            {
                result.Append(" RETURNING *");
            }

            return result.ToString();
        }
    }
}
