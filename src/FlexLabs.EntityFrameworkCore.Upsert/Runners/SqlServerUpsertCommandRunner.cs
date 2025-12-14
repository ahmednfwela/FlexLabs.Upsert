using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using FlexLabs.EntityFrameworkCore.Upsert.Internal.Expressions;

namespace FlexLabs.EntityFrameworkCore.Upsert.Runners
{
    /// <summary>
    /// Upsert command runner for the Microsoft.EntityFrameworkCore.SqlServer provider
    /// </summary>
    public class SqlServerUpsertCommandRunner : RelationalUpsertCommandRunner
    {
        /// <inheritdoc/>
        public override bool Supports(string providerName) => providerName == "Microsoft.EntityFrameworkCore.SqlServer";
        /// <inheritdoc/>
        protected override string EscapeName(string name) => "[" + name + "]";
        /// <inheritdoc/>
        protected override string? SourcePrefix => "[S].";
        /// <inheritdoc/>
        protected override string? TargetPrefix => "[T].";
        /// <inheritdoc/>
        protected override int? MaxQueryParams => 2090;

        /// <inheritdoc/>
        public override string GenerateCommand(
            string tableName,
            ICollection<ICollection<(string ColumnName, ConstantValue Value, string? DefaultSql, bool AllowInserts)>> entities,
            ICollection<(string ColumnName, bool IsNullable)> joinColumns,
            ICollection<(string ColumnName, IKnownValue Value)>? updateExpressions,
            KnownExpression? updateCondition,
            bool returnResult = false)
        {
            var updateConditionSql = updateCondition != null ? ExpandExpression(updateCondition) : null;

            var result = new StringBuilder();
            result.Append(CultureInfo.InvariantCulture, $"MERGE INTO {tableName} WITH (HOLDLOCK) AS [T] USING ( VALUES (");
            result.Append(string.Join("), (", entities.Select(ec => string.Join(", ", ec.Select(e => e.DefaultSql ?? Parameter(e.Value.ArgumentIndex))))));
            result.Append($") ) AS [S] (");
            result.Append(string.Join(", ", entities.First().Select(e => EscapeName(e.ColumnName))));
            result.Append(") ON ");
            result.Append(string.Join(" AND ", joinColumns.Select(c => c.IsNullable
                ? $"(([S].[{c.ColumnName}] IS NULL AND [T].[{c.ColumnName}] IS NULL) OR ([S].[{c.ColumnName}] IS NOT NULL AND [T].[{c.ColumnName}] = [S].[{c.ColumnName}]))"
                : $"[T].[{c.ColumnName}] = [S].[{c.ColumnName}]")));
            result.Append(" WHEN NOT MATCHED BY TARGET THEN INSERT (");
            result.Append(string.Join(", ", entities.First().Where(e => e.AllowInserts).Select(e => EscapeName(e.ColumnName))));
            result.Append(") VALUES (");
            result.Append(string.Join(", ", entities.First().Where(e => e.AllowInserts).Select(e => EscapeName(e.ColumnName))));
            result.Append(')');
            if (updateExpressions != null)
            {
                result.Append(" WHEN MATCHED");
                if (updateConditionSql != null && !returnResult)
                    result.Append(CultureInfo.InvariantCulture, $" AND {updateConditionSql}");

                result.Append(" THEN UPDATE SET ");

                if (updateCondition != null && returnResult)
                {
                    // WHEN MATCHED AND <condition> would suppress OUTPUT rows when condition is false.
                    // For RunAndReturn, always perform UPDATE but preserve values when the condition is false.
                    result.Append(string.Join(", ", updateExpressions.Select(e =>
                        $"{EscapeName(e.ColumnName)} = CASE WHEN {updateConditionSql} THEN {ExpandValue(e.Value)} ELSE {TargetPrefix}{EscapeName(e.ColumnName)}{TargetSuffix} END")));
                }
                else
                {
                    result.Append(string.Join(", ", updateExpressions.Select(e => $"{EscapeName(e.ColumnName)} = {ExpandValue(e.Value)}")));
                }
            }
            if (returnResult)
            {
                result.Append(" OUTPUT inserted.*");
            }
            result.Append(';');
            return result.ToString();
        }
    }
}
