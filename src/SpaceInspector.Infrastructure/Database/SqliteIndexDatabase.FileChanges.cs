using SpaceInspector.Core.Models;
namespace SpaceInspector.Infrastructure.Database;

public sealed partial class SqliteIndexDatabase
{
    public Task CheckFileChangeAsync(FileChangePlan plan) => Task.Run(() => {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Files WHERE FileId=$id AND FullPath=$path COLLATE NOCASE AND FileSize=$size AND ModifiedTimeUtc=$modified AND VolumeIdentifier=$volume AND (ScanStatus='Normal' OR ($undo=1 AND ScanStatus='MovedOutsideScope'))";
        command.Parameters.AddWithValue("$undo", plan.UndoOf is null ? 0 : 1);
        command.Parameters.AddWithValue("$id", plan.Source.FileId); command.Parameters.AddWithValue("$path", plan.Source.FullPath);
        command.Parameters.AddWithValue("$size", plan.Source.Size); command.Parameters.AddWithValue("$modified", plan.Source.ModifiedUtc);
        command.Parameters.AddWithValue("$volume", plan.Source.VolumeIdentifier!);
        if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new IOException("작업 기록과 선택 파일이 다릅니다. 다시 조회하세요.");
        command.Parameters.Clear(); command.Parameters.AddWithValue("$id", plan.Source.FileId); command.Parameters.AddWithValue("$target", plan.TargetPath);
        command.CommandText = "SELECT COUNT(*) FROM Files WHERE FileId<>$id AND FullPath=$target COLLATE NOCASE";
        if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw new IOException("변경 경로에 기존 작업 기록이 있습니다. 다른 이름을 선택하세요.");
    });

    public Task ApplyFileChangeAsync(FileChangePlan plan) => Task.Run(() => {
        using var connection = Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", plan.Source.FileId);
        command.CommandText = "SELECT DuplicateGroupId FROM Files WHERE FileId=$id";
        var group = command.ExecuteScalar() as string;
        var target = plan.TargetPath; var root = Path.GetPathRoot(target)!; var relative = Path.GetRelativePath(root, target);
        command.Parameters.AddWithValue("$old", plan.Source.FullPath); command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$name", Path.GetFileName(target)); command.Parameters.AddWithValue("$ext", Path.GetExtension(target).ToLowerInvariant());
        command.Parameters.AddWithValue("$parent", Path.GetDirectoryName(target)!); command.Parameters.AddWithValue("$root", root);
        command.Parameters.AddWithValue("$relative", relative); command.Parameters.AddWithValue("$physical", plan.Destination.PhysicalId!);
        command.Parameters.AddWithValue("$key", "volume|" + plan.TargetVolume + "|" + relative);
        command.Parameters.AddWithValue("$volume", plan.TargetVolume);
        command.Parameters.AddWithValue("$modified", plan.Destination.ModifiedUtc);
        command.Parameters.AddWithValue("$created", (object?)plan.TargetCreatedUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("$attributes", (object?)plan.TargetAttributes ?? DBNull.Value);
        command.Parameters.AddWithValue("$cross", plan.IsCrossVolume ? 1 : 0);
        command.Parameters.AddWithValue("$preserve", plan.RemainsInScope && plan.UndoOf is null ? 1 : 0);
        command.Parameters.AddWithValue("$inside", plan.RemainsInScope ? 1 : 0);
        command.CommandText = """
            UPDATE Files SET FullPath=$target,FileName=$name,Extension=$ext,ParentDirectory=$parent,Drive=$root,
                VolumeRelativePath=$relative,IndexKey=$key,FileSystemIdentifier=$physical,VolumeIdentifier=$volume,ModifiedTimeUtc=$modified,
                CreationTimeUtc=COALESCE($created,CreationTimeUtc),Attributes=COALESCE($attributes,Attributes),
                AllocatedSize=CASE WHEN $cross=1 THEN NULL ELSE AllocatedSize END,
                ScanStatus=CASE WHEN $inside=1 THEN 'Normal' ELSE 'MovedOutsideScope' END,
                ScanError=CASE WHEN $inside=1 THEN NULL ELSE '사용자 이동 · 작업 범위 밖' END,
                DuplicateGroupId=CASE WHEN $preserve=1 THEN DuplicateGroupId ELSE NULL END,
                DuplicateRunId=CASE WHEN $preserve=1 THEN DuplicateRunId ELSE NULL END,
                DuplicateStatus=CASE WHEN $preserve=1 THEN DuplicateStatus ELSE 'Unverified' END,
                PartialHash=CASE WHEN $preserve=1 THEN PartialHash ELSE NULL END,
                FullHash=CASE WHEN $preserve=1 THEN FullHash ELSE NULL END
            WHERE FileId=$id AND (FullPath=$old COLLATE NOCASE OR FullPath=$target COLLATE NOCASE);
            """;
        if (command.ExecuteNonQuery() != 1) throw new IOException("변경할 작업 기록을 찾을 수 없습니다. 변경 기록 복구가 필요합니다.");
        if ((!plan.RemainsInScope || plan.UndoOf is not null) && group is not null)
        {
            command.Parameters.Clear(); command.Parameters.AddWithValue("$group", group);
            command.CommandText = "UPDATE Files SET DuplicateGroupId=NULL,DuplicateStatus='NotDuplicate' WHERE DuplicateGroupId=$group AND (SELECT COUNT(*) FROM Files WHERE DuplicateGroupId=$group)<2";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    });
}
