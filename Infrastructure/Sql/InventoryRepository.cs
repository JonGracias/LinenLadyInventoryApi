using Microsoft.Data.SqlClient;

namespace LinenLady.Inventory.Functions.Infrastructure.Sql;

public sealed class InventoryRepository : IInventoryRepository
{
    private readonly string _connStr;

    public InventoryRepository(string connStr)
    {
        _connStr = connStr;
    }

    public async Task<bool> SoftDelete(int inventoryId, CancellationToken ct)
    {
        const string sql = @"
UPDATE inv.Inventory
SET IsDeleted = 1
WHERE InventoryId = @InventoryId AND IsDeleted = 0;
SELECT @@ROWCOUNT;
";

        using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@InventoryId", System.Data.SqlDbType.Int) { Value = inventoryId });

        var rows = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        return rows > 0;
    }
}
