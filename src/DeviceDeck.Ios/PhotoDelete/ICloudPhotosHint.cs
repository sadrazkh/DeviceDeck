using Microsoft.Data.Sqlite;

namespace DeviceDeck.Ios.PhotoDelete;

/// <summary>
/// Whether the phone's library shows signs of iCloud Photos, from the copy of Photos.sqlite the photo index already made
/// (nothing is copied for this): assets with an iCloud id. Only picks the words of the "can't delete" reason.
/// </summary>
internal static class ICloudPhotosHint
{
    /// <summary>True when some asset has an iCloud id; null when the copy is missing or unreadable.</summary>
    public static bool? Read(string photosDatabase)
    {
        if (!File.Exists(photosDatabase))
        {
            return null;
        }

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = photosDatabase,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            foreach (var table in new[] { "ZASSET", "ZGENERICASSET" })
            {
                var columns = Columns(connection, table);
                var test = columns.Contains("ZCLOUDASSETGUID") ? "ZCLOUDASSETGUID IS NOT NULL AND ZCLOUDASSETGUID <> ''"
                    : columns.Contains("ZCLOUDLOCALSTATE") ? "ZCLOUDLOCALSTATE = 1"
                    : null;
                if (test is null)
                {
                    continue;
                }

                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {table} WHERE {test})";
                return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
            }

            return null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static HashSet<string> Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }
}
