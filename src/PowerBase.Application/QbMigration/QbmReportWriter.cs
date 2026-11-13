using ClosedXML.Excel;

namespace PowerBase.Application.QbMigration;

/// <summary>Writes the migration report workbook: Summary, Tables, Not Migrated, Broken Links, Fields, Chunks, Issues, Users,
/// Dead Letters. Cells hold values computed here (the report is a snapshot made on a server with no spreadsheet engine).</summary>
public static class QbmReportWriter
{
    private const string Count = "#,##0", Diff = "#,##0;-#,##0;0", Pct = "0.00%", Time = "yyyy-mm-dd hh:mm:ss", Dec = "#,##0.0";
    private static readonly XLColor Navy = XLColor.FromHtml("#1F3864"), Band = XLColor.FromHtml("#D9E1F2"),
        Good = XLColor.FromHtml("#E2EFDA"), Warn = XLColor.FromHtml("#FFF2CC"), Bad = XLColor.FromHtml("#FCE4D6"), Grey = XLColor.FromHtml("#595959");

    private sealed record Col(string Header, double Width, string? Format = null);

    public static void Write(QbmReport report, Stream output)
    {
        using var wb = new XLWorkbook();
        wb.Style.Font.FontName = "Arial";
        wb.Style.Font.FontSize = 10;

        var tablesByMap = report.Tables.ToDictionary(t => t.TableMapId);
        var loadedQbTables = report.Tables.Where(t => t.Loaded).Select(t => t.QbTableId).ToHashSet();
        var notMigrated = QbmReportRules.NotMigrated(report);
        var summary = wb.Worksheets.Add("Summary");

        var tables = report.Tables.OrderByDescending(t => t.QbRecords ?? 0).ThenBy(t => t.Name).ToList();
        var nmByTable = notMigrated.Where(n => n.QbTableId is not null).GroupBy(n => n.QbTableId!).ToDictionary(g => g.Key, g => g.Count());
        var fieldsByTable = report.Fields.GroupBy(f => f.TableMapId).ToDictionary(g => g.Key, g => g.GroupBy(f => f.Action).ToDictionary(a => a.Key, a => a.Count()));
        var chunksByTable = report.Chunks.GroupBy(ch => ch.QbTableId).ToDictionary(g => g.Key, g => g.ToList());
        Sheet(wb, "Tables",
            [new("QB table id", 12), new("Table", 26), new("PB table id", 9), new("Tier", 6), new("Run", 6), new("Status", 10),
             new("QB records", 12, Count), new("PB records", 12, Count), new("Difference", 10, Diff), new("% migrated", 10, Pct),
             new("QB min Record ID", 11, Count), new("QB max Record ID", 11, Count), new("PB min Record ID", 11, Count), new("PB max Record ID", 11, Count),
             new("Gate 1 count", 9), new("Gate 2 Record ID range", 11), new("Gate 3 values", 11), new("Gate 4 broken links", 10, Count),
             new("Gate 7 encryption", 10), new("Validated (UTC)", 18, Time), new("Records compared", 12, Count),
             new("Fields loaded", 9, Count), new("Address parts", 9, Count), new("System fields", 9, Count), new("Computed fields", 9, Count),
             new("Fields skipped", 9, Count), new("Fields blocked", 9, Count), new("Not-migrated items", 11, Count),
             new("Chunks", 8, Count), new("Load attempts", 9, Count), new("Load seconds (sum of chunks)", 12, Dec)],
            tables.Select(t =>
            {
                var actions = fieldsByTable.GetValueOrDefault(t.TableMapId) ?? [];
                var chunks = chunksByTable.GetValueOrDefault(t.QbTableId) ?? [];
                var qb = t.QbRecords ?? 0;
                return new object?[]
                {
                    t.QbTableId, t.Name, t.PbTableId, t.Tier, t.RunId, t.Status, t.QbRecords, t.PbRecords, t.PbRecords - qb,
                    qb == 0 ? (t.PbRecords == 0 ? 1d : null) : (double)t.PbRecords / qb,
                    Id(t.QbMinId), Id(t.QbMaxId), t.PbMinId, t.PbMaxId,
                    PassFail(t.Gate1), PassFail(t.Gate2),
                    t.Gate3Differences is { } d ? (d == 0 ? "Pass" : $"Fail ({d:N0})") : "Not run",
                    t.BrokenLinks,
                    t.Gate7NotEncrypted is { } e ? (e == 0 ? "Pass" : $"Fail ({e:N0})") : "Not run",
                    t.ValidatedOn, t.ComparedRecords,
                    actions.GetValueOrDefault("load"), actions.GetValueOrDefault("address-part"), actions.GetValueOrDefault("system"),
                    actions.GetValueOrDefault("computed"), actions.GetValueOrDefault("skip"), actions.GetValueOrDefault("block"),
                    nmByTable.GetValueOrDefault(t.QbTableId), chunks.Count, chunks.Sum(ch => ch.Attempts), chunks.Sum(ch => ch.Seconds ?? 0),
                };
            }),
            ws =>
            {
                Comment(ws.Cell(1, 17), "From the table's latest validation: every value of every record (or the sampled chunks) re-read from QuickBase and compared, decrypted.");
                Comment(ws.Cell(1, 18), "Exact count of links whose parent record doesn't exist in PowerBase. Check them in QuickBase: such links are usually broken there too.");
                foreach (var col in new[] { 15, 16, 17, 19 })
                    foreach (var cell in ws.Column(col).CellsUsed().Skip(1))
                        cell.Style.Fill.BackgroundColor = cell.GetString().StartsWith("Pass") ? Good : cell.GetString() == "Not run" ? Warn : Bad;
            });

        Sheet(wb, "Not Migrated",
            [new("Category", 20), new("Table", 20), new("QB table id", 12), new("Record ID", 10, Count), new("QB field id", 9), new("Field", 26),
             new("Records affected", 10, Count), new("What was not migrated", 44), new("Why", 60), new("Next step", 34)],
            notMigrated.Take(report.RowCap).Select(n => new object?[] { n.Category, n.Table, n.QbTableId, n.RecordId, n.QbFid, n.Field, n.RecordsAffected, n.What, n.Why, n.NextStep }),
            ws =>
            {
                ws.Columns(8, 10).Style.Alignment.WrapText = true;
                foreach (var row in ws.RowsUsed().Skip(1))
                    row.Cell(1).Style.Fill.BackgroundColor = row.Cell(7).TryGetValue<double>(out var n) && n == 0 ? Good : Warn;
            });

        Sheet(wb, "Broken Links",
            [new("Table", 18), new("QB table id", 12), new("Link field id", 10), new("Parent table", 18), new("Record ID", 10, Count),
             new("Points to Record ID", 14, Count), new("Note", 62)],
            report.BrokenLinks.Select(b => new object?[] { b.Table, b.QbTableId, b.LinkFid, b.ParentTable, b.RecordId, b.PointsTo,
                "Migrated as in QuickBase; no parent record with this Record ID." }));

        Sheet(wb, "Fields",
            [new("QB table id", 12), new("Table", 24), new("QB field id", 9), new("Field", 30), new("QB type", 20), new("Action", 13),
             new("PB field id", 9), new("PB type", 14), new("Reason", 70)],
            report.Fields.OrderBy(f => f.Table).ThenBy(f => f.QbFid)
                .Select(f => new object?[] { f.QbTableId, f.Table, f.QbFid, f.Label, f.QbType, f.Action, f.PbFid, f.PbType, f.Reason }));

        Sheet(wb, "Chunks",
            [new("Run", 6), new("Chunk", 8), new("QB table id", 12), new("Table", 20), new("From Record ID", 12, Count), new("To Record ID", 12, Count),
             new("Status", 8), new("Rows read", 11, Count), new("Rows written", 11, Count), new("Attempts", 8), new("Started (UTC)", 19, Time),
             new("Finished (UTC)", 19, Time), new("Seconds", 9, Dec), new("Rows per second", 12, Count), new("Last error", 50)],
            report.Chunks.OrderBy(ch => ch.RunId).ThenBy(ch => ch.Id).Select(ch => new object?[]
            {
                ch.RunId, ch.Id, ch.QbTableId, ch.Table, ch.FromRecordId, ch.ToRecordId - 1, ch.Status, ch.RowsRead, ch.RowsWritten, ch.Attempts,
                ch.StartedOn, ch.FinishedOn, ch.Seconds, ch.Seconds is > 0 && ch.RowsWritten is { } w ? w / ch.Seconds : null, ch.LastError,
            }));

        Sheet(wb, "Issues",
            [new("Issue", 9), new("Run", 6), new("Table", 20), new("Record ID", 10, Count), new("QB field id", 9), new("Severity", 9),
             new("Code", 30), new("Message", 70), new("Logged (UTC)", 19, Time), new("State", 11), new("Note", 60)],
            report.Issues.Select(i =>
            {
                var (state, note) = QbmReportRules.StateOf(i, tablesByMap, loadedQbTables);
                return new object?[] { i.Id, i.RunId, i.Table, i.RecordId, i.QbFid, i.Severity, i.Code, i.Message, i.CreatedOn, state, note };
            }));

        Sheet(wb, "Users",
            [new("PB user id", 10), new("Email", 34), new("Name", 22), new("App role", 14), new("App status", 11), new("Account active", 12),
             new("Account created (UTC)", 20, Time), new("Added to app (UTC)", 20, Time)],
            report.Users.Select(u => new object?[] { u.UserId, u.Email, u.Name, u.Role, u.AppStatus, u.Active switch { true => "Yes", false => "No", _ => null }, u.AccountCreated, u.AddedToApp }));

        Sheet(wb, "Dead Letters",
            [new("Run", 6), new("Chunk", 8), new("Table", 22), new("Reason", 18), new("Description", 70), new("Dead-lettered (UTC)", 19, Time)],
            report.DeadLetters.Select(d => new object?[] { d.RunId, d.ChunkId, d.Table, d.Reason, d.Description, d.EnqueuedOn }));

        WriteSummary(summary, report, tables, notMigrated);
        wb.SaveAs(output);
    }

    private static string CategoryNote(string category) => category switch
    {
        "Table not created" => "The QBL import didn't create the table in PowerBase.",
        "Table not loaded" => "Blocked by its field mapping.",
        "File attachment" => "Attachments move in a later stage.",
        "Field not created" => "Empty in every QuickBase record; no data lost.",
        "Field not migrated" => "See the reason per field.",
        "User value left empty" => "The QuickBase user has no PowerBase user (or no email address).",
        "Owner replaced" => "Record Owner / Last Modified By set to the migration user.",
        "Link value left empty" => "No parent record with the linked key (missing in QuickBase as well).",
        _ => "See Not Migrated.",
    };

    private static long? Id(long? id) => id is null or 0 ? null : id;
    private static string PassFail(bool pass) => pass ? "Pass" : "Fail";

    private static void WriteSummary(IXLWorksheet ws, QbmReport r, IReadOnlyList<QbmReport.Table> tables, IReadOnlyList<QbmReportRules.NotMigratedItem> nm)
    {
        ws.Column(1).Width = 46;
        ws.Column(2).Width = 20;
        ws.Column(3).Width = 80;
        ws.ShowGridLines = false;
        var row = 1;
        void Title(string text) { ws.Cell(row, 1).Value = text; ws.Cell(row, 1).Style.Font.SetBold().Font.SetFontSize(16).Font.SetFontColor(Navy); row++; }
        void Note(string text) { ws.Cell(row, 1).Value = text; ws.Cell(row, 1).Style.Font.SetItalic().Font.SetFontSize(9).Font.SetFontColor(Grey); row++; }
        void Section(string text)
        {
            row++;
            ws.Cell(row, 1).Value = text;
            ws.Range(row, 1, row, 3).Style.Fill.BackgroundColor = Band;
            ws.Cell(row, 1).Style.Font.SetBold().Font.SetFontSize(11).Font.SetFontColor(Navy);
            row++;
        }
        void Line(string label, object? value, string? format = null, string? note = null, XLColor? fill = null)
        {
            ws.Cell(row, 1).Value = label;
            var cell = ws.Cell(row, 2);
            cell.Value = XLCellValue.FromObject(value);
            cell.Style.Font.SetBold().Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right)
                .Border.SetOutsideBorder(XLBorderStyleValues.Thin).Border.SetOutsideBorderColor(XLColor.FromHtml("#BFBFBF"));
            if (format is not null) cell.Style.NumberFormat.Format = format;
            if (fill is not null) cell.Style.Fill.BackgroundColor = fill;
            if (note is not null)
            {
                ws.Cell(row, 3).Value = note;
                ws.Cell(row, 3).Style.Font.SetItalic().Font.SetFontSize(9).Font.SetFontColor(Grey).Alignment.SetWrapText();
            }
            row++;
        }

        Title("QuickBase → PowerBase migration report");
        Note($"App: {r.AppName}  ·  QuickBase {r.QbRealm} / {r.QbAppId}  ·  PowerBase tenant {r.TenantId}, app id {r.PbAppId}");
        Note($"Generated {r.GeneratedOn:dd MMM yyyy HH:mm} UTC from the migration's state, the live PowerBase data and QuickBase (read-only). Values are a snapshot of that moment.");

        var counted = tables.Where(t => t.Status != "skipped").ToList();
        long qb = counted.Sum(t => t.QbRecords ?? 0), pb = counted.Sum(t => t.PbRecords);
        Section("Records");
        Line("QuickBase records", qb, Count, "Per table, as QuickBase reported when the load was planned.");
        Line("PowerBase records", pb, Count, "Rows counted directly in the PowerBase data tables (not deleted).");
        Line("Difference", pb - qb, Diff, "PowerBase minus QuickBase.", pb == qb ? Good : Bad);
        Line("% migrated", qb == 0 ? 1d : (double)pb / qb, Pct, null, pb == qb ? Good : Warn);
        Line("Tables", counted.Count, Count);
        Line("Tables loaded", counted.Count(t => t.Loaded), Count);
        Line("Tables not loaded (blocked, not found, or still loading)", counted.Count(t => !t.Loaded) + r.TablesNotFound.Count, Count, null,
            counted.All(t => t.Loaded) && r.TablesNotFound.Count == 0 ? Good : Warn);
        Line("Tables with records / empty in QuickBase", $"{counted.Count(t => (t.QbRecords ?? 0) > 0)} / {counted.Count(t => (t.QbRecords ?? 0) == 0)}");
        Line("Tables whose count matches", counted.Count(t => t.Gate1), Count);

        Section("Load performance");
        Line("Runs", r.Runs.Count, Count, string.Join("; ", r.Runs.Select(x => $"run {x.Id}: {x.Status}{(x.Control is { } ctl ? $" ({ctl})" : "")}, started {x.CreatedOn:dd MMM HH:mm}")));
        Line("Chunks", r.Chunks.Count, Count, "Record ID ranges, each loaded as one transaction.");
        Line("Chunks done / pending / failed", $"{r.Chunks.Count(c => c.Status == "done"):N0} / {r.Chunks.Count(c => c.Status is "pending" or "running"):N0} / {r.Chunks.Count(c => c.Status == "failed"):N0}",
            null, null, r.Chunks.All(c => c.Status == "done") ? Good : Warn);
        Line("Load attempts", r.Chunks.Sum(c => c.Attempts), Count, "More than the chunk count means some chunks were retried.");
        foreach (var run in r.Runs)
        {
            var chunks = r.Chunks.Where(c => c.RunId == run.Id && c.StartedOn is not null).ToList();
            if (chunks.Count == 0) continue;
            var start = chunks.Min(c => c.StartedOn!.Value);
            var end = chunks.Max(c => c.FinishedOn ?? c.StartedOn!.Value);
            var rows = chunks.Where(c => c.Status == "done").Sum(c => (long)(c.RowsWritten ?? 0));
            var minutes = (end - start).TotalMinutes;
            Line($"Run {run.Id}: records loaded", rows, Count, $"From {start:yyyy-MM-dd HH:mm:ss} to {end:yyyy-MM-dd HH:mm:ss} UTC (first chunk start to last chunk end).");
            Line($"Run {run.Id}: wall-clock minutes", minutes, Dec);
            Line($"Run {run.Id}: records per second", minutes > 0 ? rows / (minutes * 60) : null, Count, "QuickBase read + transform + encrypt + write.");
        }
        Line("Dead-letter messages", r.DeadLetters.Count, Count, r.DeadLetters.Count == 0 ? "Nothing waiting for a retry." : "See Dead Letters; retry with: retry-dlq --run <id>.",
            r.DeadLetters.Count == 0 ? Good : Bad);

        Section("Validation gates (tables passing)");
        Line("Gate 1 – record count", $"{counted.Count(t => t.Gate1)} of {counted.Count}", null, "PowerBase count = QuickBase count.");
        Line("Gate 2 – Record ID range", $"{counted.Count(t => t.Gate2)} of {counted.Count}", null, "Lowest and highest Record ID are the same (Record IDs kept 1:1).");
        Line("Gate 3 – every value compared", $"{counted.Count(t => t.Gate3Differences == 0)} of {counted.Count}", null,
            $"Not run on {counted.Count(t => t.Gate3Differences is null)} table(s). Values decrypted and compared with QuickBase.");
        Line("Gate 4 – tables with no broken links", $"{counted.Count(t => t.BrokenLinks == 0)} of {counted.Count}", null,
            $"{r.BrokenLinkTotal:N0} broken link(s) in total; see Broken Links.");
        Line("Gate 7 – encryption", $"{counted.Count(t => t.Gate7NotEncrypted == 0)} of {counted.Count}", null, "Every encrypted field holds valid ciphertext.");

        Section("Fields");
        var actions = r.Fields.GroupBy(f => f.Action).ToDictionary(g => g.Key, g => g.Count());
        Line("Fields with data loaded", actions.GetValueOrDefault("load"), Count);
        Line("Address parts (combined into Address fields)", actions.GetValueOrDefault("address-part"), Count);
        Line("System fields (Record ID, dates, owner…)", actions.GetValueOrDefault("system"), Count, "Kept in PowerBase's own system columns.");
        Line("Formula / lookup / summary fields", actions.GetValueOrDefault("computed"), Count, "Not copied: PowerBase calculates them itself.");
        Line("Fields skipped", actions.GetValueOrDefault("skip"), Count, "File attachments and empty fields with no PowerBase field; see Not Migrated.");
        Line("Fields blocked", actions.GetValueOrDefault("block"), Count, "A blocked field stops its table from loading.", actions.GetValueOrDefault("block") == 0 ? Good : Bad);

        Section("Data not migrated");
        foreach (var g in nm.GroupBy(n => n.Category).OrderBy(g => g.Key))
        {
            var affected = g.Sum(n => n.RecordsAffected ?? 0);
            Line(g.Key, g.Count(), Count, $"{affected:N0} record(s) affected. {CategoryNote(g.Key)}");
        }
        if (nm.Count == 0) Line("Nothing", 0, Count, "Every table, field and value was carried over.", Good);
        Line("Records missing in PowerBase", Math.Max(0, qb - pb), Count, "QuickBase records not in PowerBase.", qb <= pb ? Good : Bad);
        if (r.ValueIssueTotal > r.ValueIssues.Count)
            Note($"Not Migrated lists {r.ValueIssues.Count:N0} of {r.ValueIssueTotal:N0} value issues (row cap).");
        if (r.IssueTotal > r.Issues.Count) Note($"Issues lists {r.Issues.Count:N0} of {r.IssueTotal:N0} entries (row cap).");
        if (r.BrokenLinkTotal > r.BrokenLinks.Count) Note($"Broken Links lists {r.BrokenLinks.Count:N0} of {r.BrokenLinkTotal:N0} (row cap).");

        Section("Sheets");
        foreach (var (name, text) in new[]
        {
            ("Tables", "One row per table: counts, Record ID ranges, gates, fields, load time."),
            ("Not Migrated", "Every table, field, file and value that was not carried over, with the reason and next step."),
            ("Broken Links", "Links whose parent record doesn't exist."),
            ("Fields", "Every QuickBase field and what happened to it."),
            ("Chunks", "Every load chunk: Record ID range, rows, attempts, time, speed, last error."),
            ("Issues", "The migration's issue log, each entry marked Current / Superseded / Resolved / Info."),
            ("Users", "Users of the PowerBase app."),
            ("Dead Letters", "Chunks the queue gave up on; retry with retry-dlq."),
        })
        {
            ws.Cell(row, 1).Value = name;
            ws.Cell(row, 3).Value = text;
            ws.Cell(row, 3).Style.Font.SetFontSize(9).Font.SetFontColor(Grey);
            row++;
        }
        ws.SheetView.FreezeRows(3);
    }

    private static void Sheet(XLWorkbook wb, string name, IReadOnlyList<Col> cols, IEnumerable<object?[]> rows, Action<IXLWorksheet>? finish = null)
    {
        var ws = wb.Worksheets.Add(name);
        for (var c = 0; c < cols.Count; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = cols[c].Header;
            cell.Style.Font.SetBold().Font.SetFontColor(XLColor.White).Fill.SetBackgroundColor(Navy)
                .Alignment.SetWrapText().Alignment.SetVertical(XLAlignmentVerticalValues.Center);
            ws.Column(c + 1).Width = cols[c].Width;
        }
        ws.Row(1).Height = 32;
        var r = 2;
        foreach (var values in rows)
        {
            for (var c = 0; c < values.Length && c < cols.Count; c++)
                if (values[c] is not null) ws.Cell(r, c + 1).Value = XLCellValue.FromObject(values[c]);
            r++;
        }
        for (var c = 0; c < cols.Count; c++)
            if (cols[c].Format is { } format && r > 2) ws.Range(2, c + 1, r - 1, c + 1).Style.NumberFormat.Format = format;
        ws.SheetView.FreezeRows(1);
        ws.Range(1, 1, Math.Max(r - 1, 1), cols.Count).SetAutoFilter();
        finish?.Invoke(ws);
    }

    private static void Comment(IXLCell cell, string text) => cell.CreateComment().AddText(text);
}
