using System.Linq.Expressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ContextBulkExtension.Helpers;
using ContextBulkExtension.Extensions;

namespace ContextBulkExtension.Helpers;

/// <summary>
/// Helper class for working with LINQ expressions in bulk operations.
/// </summary>
public static class ExpressionHelper
{
    /// <summary>
    /// Extracts property names from a MatchOn expression.
    /// Supports single property (x => x.Email) or anonymous type (x => new { x.Email, x.Username }).
    /// </summary>
    public static List<string> ExtractPropertyNamesFromExpression<T>(Expression<Func<T, object>> expression)
    {
        var propertyNames = new List<string>();

        if (expression.Body is NewExpression newExpression)
        {
            // Anonymous type: x => new { x.Email, x.Username }
            foreach (var arg in newExpression.Arguments)
            {
                if (arg is MemberExpression memberExpr)
                {
                    propertyNames.Add(memberExpr.Member.Name);
                }
            }
        }
        else if (expression.Body is MemberExpression memberExpression)
        {
            // Single property: x => x.Email
            propertyNames.Add(memberExpression.Member.Name);
        }
        else if (expression.Body is UnaryExpression unaryExpression &&
                 unaryExpression.Operand is MemberExpression unaryMember)
        {
            // Boxing conversion: x => (object)x.Id
            propertyNames.Add(unaryMember.Member.Name);
        }

        if (propertyNames.Count == 0)
        {
            throw new ArgumentException("Invalid MatchOn expression. Use either a single property (x => x.Email) or anonymous type (x => new { x.Email, x.Username }).");
        }

        return propertyNames;
    }

    /// <summary>
    /// Converts a boolean lambda expression to SQL WHERE clause with parameterized values.
    /// Supports: ==, !=, &lt;, &gt;, &lt;=, &gt;=, &amp;&amp; (AND), || (OR)
    /// Example: x =&gt; x.AccountId == 123 &amp;&amp; x.Metric == "TOU"
    /// Returns: ("(target.[AccountId] = @p0 AND target.[Metric] = @p1)", [SqlParameter("@p0", 123), SqlParameter("@p1", "TOU")])
    /// </summary>
    public static (string Sql, List<SqlParameter> Parameters) BuildWhereClauseFromExpression<T>(
        Expression<Func<T, bool>> expression,
        DbContext context) where T : class
    {
        // Get column metadata to map property names to column names
        var columns = EntityMetadataHelper.GetColumnMetadata<T>(context, includeIdentity: true);
        var columnMap = columns.ToDictionary(c => c.PropertyInfo.Name, c => c.ColumnName, StringComparer.OrdinalIgnoreCase);

        var parameters = new List<SqlParameter>();
        var sql = BuildWhereClauseRecursive(expression.Body, columnMap, parameters);
        return (sql, parameters);
    }

    /// <summary>
    /// Recursively builds SQL WHERE clause from expression tree with parameterized values.
    /// </summary>
    private static string BuildWhereClauseRecursive(
        Expression expression,
        Dictionary<string, string> columnMap,
        List<SqlParameter> parameters)
    {
        switch (expression)
        {
            case BinaryExpression binaryExpr:
                var left = BuildWhereClauseRecursive(binaryExpr.Left, columnMap, parameters);
                var right = BuildWhereClauseRecursive(binaryExpr.Right, columnMap, parameters);

                var op = binaryExpr.NodeType switch
                {
                    ExpressionType.Equal => "=",
                    ExpressionType.NotEqual => "!=",
                    ExpressionType.LessThan => "<",
                    ExpressionType.LessThanOrEqual => "<=",
                    ExpressionType.GreaterThan => ">",
                    ExpressionType.GreaterThanOrEqual => ">=",
                    ExpressionType.AndAlso => "AND",
                    ExpressionType.OrElse => "OR",
                    _ => throw new NotSupportedException($"Operator '{binaryExpr.NodeType}' is not supported in deleteScope expression.")
                };

                // Handle NULL comparisons: x.Name == null -> "x.Name IS NULL"
                if (op == "=" && right == "NULL")
                {
                    return $"{left} IS NULL";
                }
                if (op == "!=" && right == "NULL")
                {
                    return $"{left} IS NOT NULL";
                }

                // Add parentheses for AND/OR to ensure correct precedence
                if (op == "AND" || op == "OR")
                {
                    return $"({left} {op} {right})";
                }

                return $"{left} {op} {right}";

            case MemberExpression memberExpr:
                // Check if this is accessing the entity parameter (e.g., m.AccountId)
                // vs a captured variable (e.g., start, end from closure)
                if (memberExpr.Expression is ParameterExpression)
                {
                    // Property access on entity: x.AccountId -> target.[AccountId]
                    var propertyName = memberExpr.Member.Name;
                    if (!columnMap.TryGetValue(propertyName, out var columnName))
                    {
                        throw new InvalidOperationException($"Property '{propertyName}' not found in entity metadata.");
                    }
                    return $"target.{columnName.EscapeSqlIdentifier()}";
                }
                else
                {
                    // Captured variable or constant member access: evaluate to get the value
                    try
                    {
                        var lambda = Expression.Lambda(memberExpr);
                        var value = lambda.Compile().DynamicInvoke();
                        return AddParameter(value, parameters);
                    }
                    catch (Exception ex)
                    {
                        throw new NotSupportedException($"Could not evaluate captured variable '{memberExpr.Member.Name}': {ex.Message}");
                    }
                }

            case ConstantExpression constantExpr:
                // Constant value: Add as parameter instead of inlining
                return AddParameter(constantExpr.Value, parameters);

            case UnaryExpression unaryExpr when unaryExpr.NodeType == ExpressionType.Convert:
                // Type conversion: (int)x.Id
                return BuildWhereClauseRecursive(unaryExpr.Operand, columnMap, parameters);

            default:
                // For complex expressions (method calls, closures), evaluate to get the value
                if (expression is MemberExpression ||
                    expression is MethodCallExpression)
                {
                    try
                    {
                        var lambda = Expression.Lambda(expression);
                        var value = lambda.Compile().DynamicInvoke();
                        return AddParameter(value, parameters);
                    }
                    catch (Exception ex)
                    {
                        throw new NotSupportedException($"Expression type '{expression.NodeType}' could not be evaluated: {ex.Message}");
                    }
                }

                throw new NotSupportedException($"Expression type '{expression.NodeType}' is not supported in deleteScope expression.");
        }
    }

    /// <summary>
    /// Adds a value as a SQL parameter and returns the parameter placeholder.
    /// Prevents SQL injection by using parameterized queries.
    /// </summary>
    private static string AddParameter(object? value, List<SqlParameter> parameters)
    {
        if (value == null)
        {
            return "NULL";
        }

        var paramName = $"@p{parameters.Count}";
        var parameter = new SqlParameter(paramName, value);
        parameters.Add(parameter);
        return paramName;
    }
}