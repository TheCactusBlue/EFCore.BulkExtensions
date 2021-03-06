using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EFCore.BulkExtensions
{
    // public class BatchSql
    // {
    //     public string Sql;
    //     public string TableAlias;
    //     public string TableAliasSufixAs;
    //     public string TopStatement;
    //     public string LeadingComments;
    //     public IEnumerable<object> InnerParameters;
    // }
    
    public static class BatchUtil
    {
        private static readonly int SelectStatementLength = "SELECT".Length;

        // In comment are Examples of how SqlQuery is changed for Sql Batch

        // SELECT [a].[Column1], [a].[Column2], .../r/n
        // FROM [Table] AS [a]/r/n
        // WHERE [a].[Column] = FilterValue
        // --
        // DELETE [a]
        // FROM [Table] AS [a]
        // WHERE [a].[Columns] = FilterValues
        public static (string, List<object>) GetSqlDelete(IQueryable query, DbContext context)
        {
            var (sql, tableAlias, _, topStatement, leadingComments, innerParameters) = GetBatchSql(query, context, false);

            innerParameters = ReloadSqlParameters(context, innerParameters.ToList()); // Sqlite requires SqliteParameters
            tableAlias = GetDatabaseType(context) == DbServer.SqlServer ? $"[{tableAlias}]" : tableAlias;

            var resultQuery = $"{leadingComments}DELETE {topStatement}{tableAlias}{sql}";
            return (resultQuery, new List<object>(innerParameters));
        }

        // SELECT [a].[Column1], [a].[Column2], .../r/n
        // FROM [Table] AS [a]/r/n
        // WHERE [a].[Column] = FilterValue
        // --
        // UPDATE [a] SET [UpdateColumns] = N'updateValues'
        // FROM [Table] AS [a]
        // WHERE [a].[Columns] = FilterValues
        public static (string, List<object>) GetSqlUpdate(IQueryable query, DbContext context, object updateValues, List<string> updateColumns)
        {
            (string sql, string tableAlias, string tableAliasSufixAs, string topStatement, string leadingComments, IEnumerable<object> innerParameters) = GetBatchSql(query, context, isUpdate: true);
            var sqlParameters = new List<object>(innerParameters);

            string sqlSET = GetSqlSetSegment(context, updateValues.GetType(), updateValues, updateColumns, sqlParameters);

            sqlParameters = ReloadSqlParameters(context, sqlParameters); // Sqlite requires SqliteParameters

            var resultQuery = $"{leadingComments}UPDATE {topStatement}{tableAlias}{tableAliasSufixAs} {sqlSET}{sql}";
            return (resultQuery, sqlParameters);
        }

        /// <summary>
        /// get Update Sql
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="query"></param>
        /// <param name="expression"></param>
        /// <returns></returns>
        public static (string, List<object>) GetSqlUpdate<T>(IQueryable<T> query, DbContext context, Expression<Func<T, T>> expression) where T : class
        {
            return GetSqlUpdate<T>(query, context, typeof(T), expression);
        }
        public static (string, List<object>) GetSqlUpdate(IQueryable query, DbContext context, Type type, Expression<Func<object, object>> expression)
        {
            return GetSqlUpdate<object>(query, context, type, expression);
        }

        private static (string, List<object>) GetSqlUpdate<T>(IQueryable query, DbContext context, Type type, Expression<Func<T, T>> expression) where T : class
        {
            var (sql, tableAlias, tableAliasSufixAs, topStatement, leadingComments, innerParameters) = GetBatchSql(query, context, true);
            var sqlColumns = new StringBuilder();
            var sqlParameters = new List<object>(innerParameters);
            var columnNameValueDict = TableInfo.CreateInstance(GetDbContext(query), type, new List<object>(), OperationType.Read, new BulkConfig()).PropertyColumnNamesDict;
            var dbType = GetDatabaseType(context);
            CreateUpdateBody(columnNameValueDict, tableAlias, expression.Body, dbType, ref sqlColumns, ref sqlParameters);

            sqlParameters = ReloadSqlParameters(context, sqlParameters); // Sqlite requires SqliteParameters
            sqlColumns = (GetDatabaseType(context) == DbServer.SqlServer) ? sqlColumns : sqlColumns.Replace($"[{tableAlias}].", "");

            var resultQuery = $"{leadingComments}UPDATE {topStatement}{tableAlias}{tableAliasSufixAs} SET {sqlColumns} {sql}";
            return (resultQuery, sqlParameters);
        }

        public static List<object> ReloadSqlParameters(DbContext context, List<object> sqlParameters)
        {
            DbServer databaseType = GetDatabaseType(context);
            if (databaseType == DbServer.Sqlite)
            {
                var sqlParametersReloaded = new List<object>();
                foreach (var parameter in sqlParameters)
                {
                    var sqlParameter = (SqlParameter)parameter;
                    sqlParametersReloaded.Add(new SqliteParameter(sqlParameter.ParameterName, sqlParameter.Value));
                }
                return sqlParametersReloaded;
            }
            else // for SqlServer return original
            {
                return sqlParameters;
            }
        }

        public static (string, string, string, string, string, IEnumerable<object>) GetBatchSql(IQueryable query, DbContext context, bool isUpdate)
        {
            var (fullSqlQuery, innerParameters) = query.ToParametrizedSql();

            DbServer databaseType = GetDatabaseType(context);
            var (leadingComments, sqlQuery) = SplitLeadingCommentsAndMainSqlQuery(fullSqlQuery);

            string tableAlias = string.Empty;
            string tableAliasSufixAs = string.Empty;
            string topStatement = string.Empty;
            if (databaseType != DbServer.Sqlite) // when Sqlite and Deleted metod tableAlias is Empty: ""
            {
                var isSqlServer = databaseType == DbServer.SqlServer;
                string escapeSymbolEnd = isSqlServer ? "]" : "."; // SqlServer : PostrgeSql;
                string escapeSymbolStart = isSqlServer ? "[" : " "; // SqlServer : PostrgeSql;
                string tableAliasEnd = sqlQuery.Substring(SelectStatementLength,
                    sqlQuery.IndexOf(escapeSymbolEnd, StringComparison.Ordinal) - SelectStatementLength); // " TOP(10) [table_alias" / " [table_alias" : " table_alias"
                int tableAliasStartIndex = tableAliasEnd.IndexOf(escapeSymbolStart, StringComparison.Ordinal);
                tableAlias = tableAliasEnd.Substring(tableAliasStartIndex + escapeSymbolStart.Length); // "table_alias"
                topStatement = tableAliasEnd.Substring(0, tableAliasStartIndex).TrimStart(); // "TOP(10) " / if TOP not present in query this will be a Substring(0,0) == ""
            }

            int indexFrom = sqlQuery.IndexOf(Environment.NewLine, StringComparison.Ordinal);
            string sql = sqlQuery.Substring(indexFrom, sqlQuery.Length - indexFrom);
            sql = sql.Contains("{") ? sql.Replace("{", "{{") : sql; // Curly brackets have to be escaped:
            sql = sql.Contains("}") ? sql.Replace("}", "}}") : sql; // https://github.com/aspnet/EntityFrameworkCore/issues/8820

            if (isUpdate && databaseType == DbServer.Sqlite)
            {
                var match = Regex.Match(sql, @"FROM (""[^""]+"")( AS ""[^""]+"")");
                tableAlias = match.Groups[1].Value;
                tableAliasSufixAs = match.Groups[2].Value;
                sql = sql.Substring(match.Index + match.Length);
            }

            return (sql, tableAlias, tableAliasSufixAs, topStatement, leadingComments, innerParameters);
        }

        public static string GetSqlSetSegment<T>(DbContext context, T updateValues, List<string> updateColumns, List<object> parameters) where T : class, new()
        {
            var tableInfo = TableInfo.CreateInstance<T>(context, new List<T>(), OperationType.Read, new BulkConfig());
            return GetSqlSetSegment(context, tableInfo, typeof(T), updateValues, new T(), updateColumns, parameters);
        }

        public static string GetSqlSetSegment(DbContext context, Type updateValuesType, object updateValues, List<string> updateColumns, List<object> parameters)
        {
            var tableInfo = TableInfo.CreateInstance(context, updateValuesType, new List<object>(), OperationType.Read, new BulkConfig());
            return GetSqlSetSegment(context, tableInfo, updateValuesType, updateValues, Activator.CreateInstance(updateValuesType), updateColumns, parameters);
        }

        private static string GetSqlSetSegment(DbContext context, TableInfo tableInfo, Type updateValuesType, object updateValues, object defaultValues, List<string> updateColumns, List<object> parameters)
        {
            string sql = string.Empty;
            foreach (var propertyNameColumnName in tableInfo.PropertyColumnNamesDict)
            {
                string propertyName = propertyNameColumnName.Key;
                string columnName = propertyNameColumnName.Value;
                var pArray = propertyName.Split(new char[] { '.' });
                Type lastType = updateValuesType;
                PropertyInfo property = lastType.GetProperty(pArray[0]);
                if (property != null)
                {
                    object propertyUpdateValue = property.GetValue(updateValues);
                    object propertyDefaultValue = property.GetValue(defaultValues);
                    for (int i = 1; i < pArray.Length; i++)
                    {
                        lastType = property.PropertyType;
                        property = lastType.GetProperty(pArray[i]);
                        propertyUpdateValue = propertyUpdateValue != null ? property.GetValue(propertyUpdateValue) : propertyUpdateValue;
                        var lastDefaultValues = lastType.Assembly.CreateInstance(lastType.FullName);
                        propertyDefaultValue = property.GetValue(lastDefaultValues);
                    }

                    if (tableInfo.ConvertibleProperties.ContainsKey(columnName))
                    {
                        propertyUpdateValue = tableInfo.ConvertibleProperties[columnName].ConvertToProvider.Invoke(propertyUpdateValue);
                    }

                    bool isDifferentFromDefault = propertyUpdateValue != null && propertyUpdateValue?.ToString() != propertyDefaultValue?.ToString();
                    if (isDifferentFromDefault || (updateColumns != null && updateColumns.Contains(propertyName)))
                    {
                        sql += $"[{columnName}] = @{columnName}, ";
                        propertyUpdateValue = propertyUpdateValue ?? DBNull.Value;
                        parameters.Add(new SqlParameter($"@{columnName}", propertyUpdateValue));
                    }
                }
            }
            if (string.IsNullOrEmpty(sql))
            {
                throw new InvalidOperationException("SET Columns not defined. If one or more columns should be updated to theirs default value use 'updateColumns' argument.");
            }
            sql = sql.Remove(sql.Length - 2, 2); // removes last excess comma and space: ", "
            return $"SET {sql}";
        }

        /// <summary>
        /// Recursive analytic expression 
        /// </summary>
        /// <param name="tableAlias"></param>
        /// <param name="expression"></param>
        /// <param name="sqlColumns"></param>
        /// <param name="sqlParameters"></param>
        public static void CreateUpdateBody(Dictionary<string, string> columnNameValueDict, string tableAlias, Expression expression, DbServer dbType, ref StringBuilder sqlColumns, ref List<object> sqlParameters)
        {
            if (expression is MemberInitExpression memberInitExpression)
            {
                foreach (var item in memberInitExpression.Bindings)
                {
                    if (item is MemberAssignment assignment)
                    {
                        if (columnNameValueDict.TryGetValue(assignment.Member.Name, out string value))
                            sqlColumns.Append($" [{tableAlias}].[{value}]");
                        else
                            sqlColumns.Append($" [{tableAlias}].[{assignment.Member.Name}]");

                        sqlColumns.Append(" =");

                        CreateUpdateBody(columnNameValueDict, tableAlias, assignment.Expression, dbType, ref sqlColumns, ref sqlParameters);

                        if (memberInitExpression.Bindings.IndexOf(item) < (memberInitExpression.Bindings.Count - 1))
                            sqlColumns.Append(" ,");
                    }
                }
            }
            else if (expression is MemberExpression memberExpression && memberExpression.Expression is ParameterExpression)
            {
                if (columnNameValueDict.TryGetValue(memberExpression.Member.Name, out string value))
                    sqlColumns.Append($" [{tableAlias}].[{value}]");
                else
                    sqlColumns.Append($" [{tableAlias}].[{memberExpression.Member.Name}]");
            }
            else if (expression is ConstantExpression constantExpression)
            {
                var parmName = $"param_{sqlParameters.Count}";
                sqlParameters.Add(new SqlParameter(parmName, constantExpression.Value ?? DBNull.Value));
                sqlColumns.Append($" @{parmName}");
            }
            else if (expression is UnaryExpression unaryExpression)
            {
                switch (unaryExpression.NodeType)
                {
                    case ExpressionType.Convert:
                        CreateUpdateBody(columnNameValueDict, tableAlias, unaryExpression.Operand, dbType, ref sqlColumns, ref sqlParameters);
                        break;
                    case ExpressionType.Not:
                        sqlColumns.Append(" ~");//this way only for SQL Server 
                        CreateUpdateBody(columnNameValueDict, tableAlias, unaryExpression.Operand, dbType, ref sqlColumns, ref sqlParameters);
                        break;
                    default: break;
                }
            }
            else if (expression is BinaryExpression binaryExpression)
            {
                CreateUpdateBody(columnNameValueDict, tableAlias, binaryExpression.Left, dbType, ref sqlColumns, ref sqlParameters);

                switch (binaryExpression.NodeType)
                {
                    case ExpressionType.Add:
                        sqlColumns.Append(dbType == DbServer.Sqlite && IsStringConcat(binaryExpression) ? " ||" : " +");
                        break;
                    case ExpressionType.Divide:
                        sqlColumns.Append(" /");
                        break;
                    case ExpressionType.Multiply:
                        sqlColumns.Append(" *");
                        break;
                    case ExpressionType.Subtract:
                        sqlColumns.Append(" -");
                        break;
                    case ExpressionType.And:
                        sqlColumns.Append(" &");
                        break;
                    case ExpressionType.Or:
                        sqlColumns.Append(" |");
                        break;
                    case ExpressionType.ExclusiveOr:
                        sqlColumns.Append(" ^");
                        break;
                    default: break;
                }

                CreateUpdateBody(columnNameValueDict, tableAlias, binaryExpression.Right, dbType, ref sqlColumns, ref sqlParameters);
            }
            else
            {
                var value = Expression.Lambda(expression).Compile().DynamicInvoke();
                var parmName = $"param_{sqlParameters.Count}";
                sqlParameters.Add(new SqlParameter(parmName, value ?? DBNull.Value));
                sqlColumns.Append($" @{parmName}");
            }
        }

        public static DbContext GetDbContext(IQueryable query)
        {
            const BindingFlags bindingFlags = BindingFlags.NonPublic | BindingFlags.Instance;
            var queryCompiler = typeof(EntityQueryProvider).GetField("_queryCompiler", bindingFlags).GetValue(query.Provider);
            var queryContextFactory = queryCompiler.GetType().GetField("_queryContextFactory", bindingFlags).GetValue(queryCompiler);

            var dependencies = typeof(RelationalQueryContextFactory).GetField("_dependencies", bindingFlags).GetValue(queryContextFactory);
            var queryContextDependencies = typeof(DbContext).Assembly.GetType(typeof(QueryContextDependencies).FullName);
            var stateManagerProperty = queryContextDependencies.GetProperty("StateManager", bindingFlags | BindingFlags.Public).GetValue(dependencies);
            var stateManager = (IStateManager)stateManagerProperty;

#pragma warning disable EF1001 // Internal EF Core API usage.
            return stateManager.Context;
#pragma warning restore EF1001 // Internal EF Core API usage.
        }

        public static DbServer GetDatabaseType(DbContext context)
        {
            return context.Database.ProviderName.EndsWith(DbServer.Sqlite.ToString()) ? DbServer.Sqlite : DbServer.SqlServer;
        }

        internal static bool IsStringConcat(BinaryExpression binaryExpression)
        {
            var methodProperty = binaryExpression.GetType().GetProperty("Method");
            if (methodProperty == null)
            {
                return false;
            }
            var method = methodProperty.GetValue(binaryExpression) as MethodInfo;
            if (method == null)
            {
                return false;
            }
            return method.DeclaringType == typeof(string) && method.Name == nameof(string.Concat);
        }

        public static (string, string) SplitLeadingCommentsAndMainSqlQuery(string sqlQuery)
        {
            var leadingCommentsBuilder = new StringBuilder();
            var mainSqlQuery = sqlQuery;
            while (!string.IsNullOrWhiteSpace(mainSqlQuery) 
                && !mainSqlQuery.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                if (mainSqlQuery.StartsWith("--"))
                {
                    // pull off line comment
                    var indexOfNextNewLine = mainSqlQuery.IndexOf(Environment.NewLine, StringComparison.Ordinal);
                    if (indexOfNextNewLine > -1)
                    {
                        leadingCommentsBuilder.Append(mainSqlQuery.Substring(0, indexOfNextNewLine + Environment.NewLine.Length));
                        mainSqlQuery = mainSqlQuery.Substring(indexOfNextNewLine + Environment.NewLine.Length);
                        continue;
                    }
                }

                if (mainSqlQuery.StartsWith("/*"))
                {
                    var nextBlockCommentEndIndex = mainSqlQuery.IndexOf("*/", StringComparison.Ordinal);
                    if (nextBlockCommentEndIndex > -1)
                    {
                        leadingCommentsBuilder.Append(mainSqlQuery.Substring(0, nextBlockCommentEndIndex + 2));
                        mainSqlQuery = mainSqlQuery.Substring(nextBlockCommentEndIndex + 2);
                        continue;
                    }
                }

                var nextNonWhitespaceIndex = 
                    Array.FindIndex(mainSqlQuery.ToCharArray(), x => !char.IsWhiteSpace(x));

                if (nextNonWhitespaceIndex > 0)
                {
                    leadingCommentsBuilder.Append(mainSqlQuery.Substring(0, nextNonWhitespaceIndex));
                    mainSqlQuery = mainSqlQuery.Substring(nextNonWhitespaceIndex);
                    continue;
                }

                // Fallback... just find the first index of SELECT
                var selectIndex = mainSqlQuery.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase);
                if (selectIndex > 0)
                {
                    leadingCommentsBuilder.Append(mainSqlQuery.Substring(0, selectIndex));
                    mainSqlQuery = mainSqlQuery.Substring(selectIndex);
                }

                break;
            }

            return (leadingCommentsBuilder.ToString(), mainSqlQuery);
        }
    }
}
