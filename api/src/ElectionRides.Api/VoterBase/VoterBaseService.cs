using ElectionRides.Api.Common;
using ElectionRides.Api.Data;
using ElectionRides.Api.Domain;
using ElectionRides.Api.Email;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace ElectionRides.Api.VoterBase;

/// <summary>Column numbers (0-based) chosen by the city admin. Required: ID, first name, last name, ballot box.</summary>
public record ColumnMap(int IdNumber, int FirstName, int LastName, int BallotBox, int? Cluster, List<int>? Phones, int? Age, int? Street, int? BuildingNumber);

public record ImportResult(int Imported, int Skipped, Dictionary<string, int> SkipReasons, List<string> BoxesWithoutCluster);

/// <summary>Loads and deletes a city's voter base (spec 3.1, 8, 12). Deleting always makes a deletion certificate.</summary>
public class VoterBaseService(AppDbContext db, IEmailSender mail, IConfiguration config, ILogger<VoterBaseService> log)
{
    public const string AutomaticDeletion = "מחיקה אוטומטית";

    public async Task<ImportResult> ImportAsync(Guid cityId, TableFile.Table table, ColumnMap map, string fileName, AppUser by, CancellationToken ct)
    {
        var width = table.Headers.Count;
        var cols = new[] { map.IdNumber, map.FirstName, map.LastName, map.BallotBox }
            .Concat(new[] { map.Cluster, map.Age, map.Street, map.BuildingNumber }.Where(c => c is not null).Select(c => c!.Value))
            .Concat(map.Phones ?? []);
        if (cols.Any(c => c < 0 || c >= width)) throw new InvalidDataException("אחת העמודות שנבחרו לא קיימת בקובץ");

        var clusters = await db.Clusters.Where(c => c.CityId == cityId).ToListAsync(ct);
        var byBox = await db.BallotBoxes.Where(b => b.CityId == cityId).ToDictionaryAsync(b => b.Number, b => b.ClusterId, ct);
        Guid? ClusterFromText(string v)
        {
            v = Rules.NormalizeName(v);
            if (v.Length == 0) return null;
            return clusters.FirstOrDefault(c => (c.Number is { } n && n.ToString() == v) || c.ShortName == v || c.Name == v)?.Id;
        }

        var voters = new List<Voter>();
        var seen = new HashSet<string>();
        var skips = new Dictionary<string, int>();
        var noCluster = new SortedSet<string>(StringComparer.Ordinal);
        void Skip(string why) => skips[why] = skips.GetValueOrDefault(why) + 1;
        string Cell(string[] row, int i) => i < row.Length ? row[i].Trim() : "";

        foreach (var row in table.Rows)
        {
            var idText = Rules.DigitsOnly(Cell(row, map.IdNumber));
            if (!Rules.IsValidIdNumber(idText)) { Skip("ת\"ז לא תקינה"); continue; }
            var id = Rules.NormalizeId(idText);
            if (!seen.Add(id)) { Skip("ת\"ז שמופיעה פעמיים"); continue; }
            var first = Rules.NormalizeName(Cell(row, map.FirstName));
            var last = Rules.NormalizeName(Cell(row, map.LastName));
            if (first.Length == 0 && last.Length == 0) { Skip("בלי שם"); continue; }
            var box = Cell(row, map.BallotBox).Replace(" ", "");
            if (box.EndsWith(".0")) box = box[..^2];
            if (box.Length == 0) { Skip("בלי קלפי"); continue; }

            var cluster = map.Cluster is { } cc ? ClusterFromText(Cell(row, cc)) : null;
            cluster ??= byBox.TryGetValue(box, out var cb) ? cb : null;
            if (cluster is null) noCluster.Add(box);

            var v = new Voter
            {
                CityId = cityId, IdNumber = id, FirstName = Trim(first, 60), LastName = Trim(last, 60), BallotBox = Trim(box, 10),
                ClusterId = cluster,
                Age = map.Age is { } ac && int.TryParse(Cell(row, ac), out var age) && age is > 0 and < 130 ? age : null,
                Street = map.Street is { } sc ? NullIfEmpty(Trim(Rules.NormalizeName(Cell(row, sc)), 80)) : null,
                BuildingNumber = map.BuildingNumber is { } bc ? NullIfEmpty(Trim(Cell(row, bc), 10)) : null,
            };
            foreach (var pc in map.Phones ?? [])
            {
                var phone = Rules.DigitsOnly(Cell(row, pc));
                if (Rules.IsValidPhone(phone) && v.Phones.All(p => p.Phone != phone))
                    v.Phones.Add(new VoterPhone { VoterId = v.Id, Phone = phone });
            }
            voters.Add(v);
        }
        if (voters.Count == 0) throw new InvalidDataException("לא נמצאה אף שורה תקינה בקובץ. כדאי לבדוק את התאמת העמודות.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Loading a new file replaces the old base: the old one is deleted, with a certificate.
        if (await db.VoterBases.AnyAsync(b => b.CityId == cityId, ct))
            await DeleteAsync(cityId, by.FullName, "הוחלף בקובץ חדש", ct, sendMail: true);
        await BulkInsert(voters, ct);
        db.VoterBases.Add(new VoterBaseInfo { CityId = cityId, FileName = Trim(fileName, 200), VoterCount = voters.Count, LoadedBy = by.FullName });
        Audit.Add(db, by, "base.import", $"{fileName}: נטענו {voters.Count}, דולגו {skips.Values.Sum()}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ImportResult(voters.Count, skips.Values.Sum(), skips, noCluster.Take(30).ToList());
    }

    /// <summary>Hundreds of thousands of rows: written with PostgreSQL's binary copy, not one by one.</summary>
    async Task BulkInsert(List<Voter> voters, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using (var w = await conn.BeginBinaryImportAsync(
            "COPY \"Voters\" (\"Id\", \"CityId\", \"IdNumber\", \"FirstName\", \"LastName\", \"BallotBox\", \"ClusterId\", \"Age\", \"Street\", \"BuildingNumber\") FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var v in voters)
            {
                await w.StartRowAsync(ct);
                await w.WriteAsync(v.Id, NpgsqlDbType.Uuid, ct);
                await w.WriteAsync(v.CityId, NpgsqlDbType.Uuid, ct);
                await w.WriteAsync(v.IdNumber, NpgsqlDbType.Varchar, ct);
                await w.WriteAsync(v.FirstName, NpgsqlDbType.Varchar, ct);
                await w.WriteAsync(v.LastName, NpgsqlDbType.Varchar, ct);
                await w.WriteAsync(v.BallotBox, NpgsqlDbType.Varchar, ct);
                if (v.ClusterId is { } c) await w.WriteAsync(c, NpgsqlDbType.Uuid, ct); else await w.WriteNullAsync(ct);
                if (v.Age is { } a) await w.WriteAsync(a, NpgsqlDbType.Integer, ct); else await w.WriteNullAsync(ct);
                if (v.Street is { } s) await w.WriteAsync(s, NpgsqlDbType.Varchar, ct); else await w.WriteNullAsync(ct);
                if (v.BuildingNumber is { } bn) await w.WriteAsync(bn, NpgsqlDbType.Varchar, ct); else await w.WriteNullAsync(ct);
            }
            await w.CompleteAsync(ct);
        }
        await using (var w = await conn.BeginBinaryImportAsync("COPY \"VoterPhones\" (\"VoterId\", \"Phone\") FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var p in voters.SelectMany(v => v.Phones))
            {
                await w.StartRowAsync(ct);
                await w.WriteAsync(p.VoterId, NpgsqlDbType.Uuid, ct);
                await w.WriteAsync(p.Phone, NpgsqlDbType.Varchar, ct);
            }
            await w.CompleteAsync(ct);
        }
    }

    /// <summary>
    /// Deletes the city's base and makes a deletion certificate, saved and emailed to the party's privacy officer
    /// and the city admins. Requests already taken are not deleted (spec 3.1).
    /// </summary>
    public async Task<DeletionCertificate?> DeleteAsync(Guid cityId, string by, string? why, CancellationToken ct, bool sendMail = true)
    {
        var info = await db.VoterBases.FirstOrDefaultAsync(b => b.CityId == cityId, ct);
        if (info is null) return null;
        var city = await db.Cities.FirstAsync(c => c.Id == cityId, ct);
        var op = await db.Operations.Where(o => o.Cities.Any(c => c.Id == cityId)).OrderByDescending(o => o.IsActive).ThenByDescending(o => o.ElectionDay).FirstOrDefaultAsync(ct);
        var party = await db.PartySettings.SingleAsync(ct);
        var admins = await db.Users.Where(u => u.CityId == cityId && u.Role == UserRole.CityAdmin && u.IsActive).Select(u => u.Email).ToListAsync(ct);
        var recipients = admins.Prepend(party.PrivacyOfficerEmail).Where(e => !string.IsNullOrWhiteSpace(e)).Distinct().ToList();

        var count = await db.Voters.Where(v => v.CityId == cityId).ExecuteDeleteAsync(ct);
        db.VoterBases.Remove(info);

        var backupDays = config.GetValue("Backups:RetentionDays", 7);
        var backupsGoneBy = DateTimeOffset.UtcNow.AddDays(backupDays);
        var cert = new DeletionCertificate
        {
            CityId = cityId, CityName = city.Name, FileName = info.FileName, VoterCount = count, DeletedBy = by,
            OperationText = op is null ? null : $"{(op.Type == ElectionType.Knesset ? "בחירות לכנסת" : "בחירות לרשויות המקומיות")} {(city.ElectionDayOverride ?? op.ElectionDay):dd.MM.yyyy}",
            WhatWasDeleted = $"כל {count} רשומות המצביעים מהקובץ {info.FileName}: ת\"ז, שמות, קלפי, אשכול, טלפונים, גיל וכתובת. " +
                             $"הרשומות נמחקו מיד ממסד הנתונים. בגיבויים האוטומטיים הן נשמרות עד תום תקופת שמירת הגיבויים ({backupDays} ימים), ונמחקות עד {backupsGoneBy:dd.MM.yyyy}. " +
                             "פניות שכבר נקלטו לא נמחקו." + (why is null ? "" : $" סיבה: {why}."),
            SentTo = recipients.Count == 0 ? "לא הוגדר ממונה פרטיות ואין מנהל עיר עם אימייל" : string.Join(", ", recipients),
        };
        db.DeletionCertificates.Add(cert);
        Audit.Add(db, null, "base.delete", $"{info.FileName}: נמחקו {count} מצביעים ({by})", cityId);
        await db.SaveChangesAsync(ct);

        if (sendMail)
            foreach (var to in recipients)
                try
                {
                    await mail.SendAsync(new EmailMessage(to, $"אישור מחיקה מס' {cert.Number}: בייס המצביעים של {city.Name}", CertificateText(cert)), ct);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Deletion certificate {Number} was not emailed to {To}", cert.Number, to);
                }
        return cert;
    }

    public static string CertificateText(DeletionCertificate c) =>
        $"אישור מחיקה מס' {c.Number}\n\nעיר: {c.CityName}\nבחירות: {c.OperationText ?? "-"}\nקובץ: {c.FileName}\nמספר מצביעים: {c.VoterCount}\n" +
        $"מתי נמחק: {TimeZoneInfo.ConvertTime(c.DeletedAt, TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem")):dd.MM.yyyy HH:mm}\nמי מחק: {c.DeletedBy}\n\nמה נמחק: {c.WhatWasDeleted}\n\nנשלח אל: {c.SentTo}";

    static string Trim(string s, int max) => s.Length > max ? s[..max] : s;
    static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
