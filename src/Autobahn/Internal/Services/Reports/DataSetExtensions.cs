using System.Data;

namespace Autobahn.Internal.Services.Reports;

/// <summary>ADO.NET collections as arrays, so the report writers can use LINQ on them.</summary>
internal static class DataSetExtensions
{
    public static DataTable[] GetTables(this DataSet dataSet) => dataSet.Tables.Cast<DataTable>().ToArray();

    public static DataColumn[] GetColumns(this DataTable dataTable) => dataTable.Columns.Cast<DataColumn>().ToArray();

    public static DataRow[] GetRows(this DataTable dataTable) => dataTable.Rows.Cast<DataRow>().ToArray();

    public static string GetColumnCaptionOrName(this DataColumn column) =>
        string.IsNullOrEmpty(column.Caption) ? column.ColumnName : column.Caption;
}
