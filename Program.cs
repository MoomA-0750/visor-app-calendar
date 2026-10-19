using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ical.Net.DataTypes;
using VisorSdk;

// visor のアプリ「カレンダー」: iCal 形式の URL を自分で読み（Claude を通さない）、今日から 14 日分の予定を日ごとに並べる。
// 15 分おきと、窓を開いたときに読み直す。始まる 10 分前に visor の通知を出す（窓を開いていなくても。visor を起こしたときから動く）。
// 読むカレンダーは VISOR_APP_DATA の calendars.json（前の visor の %LOCALAPPDATA%\visor\calendars.json があれば引き継ぐ）。

const int Days = 14;
var refreshEvery = TimeSpan.FromMinutes(15);
var remindBefore = TimeSpan.FromMinutes(10);
string[] week = ["日", "月", "火", "水", "木", "金", "土"];

var app = new VisorApp();
var path = Path.Combine(VisorApp.DataDir, "calendars.json");
var gate = new object();
var sources = LoadSources();
List<Item> items = [];
List<string> errors = [];
DateTime? loadedAt = null;
var loading = false;
var nextRefresh = DateTime.UtcNow;
var reminded = new HashSet<string>();

app.Event += id =>
{
    if (id == "refresh") _ = Refresh();
};
app.Visible += on =>
{
    if (on) _ = Refresh();
};

app.Tool("list_events", "visor のカレンダー（iCal の URL から読んだもの）の予定を見る。", a =>
{
    var days = (int)(a["days"]?.GetValue<double>() ?? 7);
    lock (gate) return Agenda(days);
}, ("days", "number"));
app.Tool("add_calendar", "visor のカレンダーに、読むカレンダー（iCal 形式の URL。Google なら「設定 → カレンダーの統合 → iCal 形式の非公開 URL」）を足す。", a =>
{
    var name = a["name"]?.GetValue<string>() ?? "カレンダー";
    var url = a["url"]?.GetValue<string>() ?? throw new ArgumentException("url が無い");
    lock (gate)
    {
        sources.RemoveAll(s => s.Name == name);
        sources.Add(new Source(name, url));
        SaveSources();
    }
    _ = Refresh();
    return $"{name} を足した。読み直している";
}, ("name", "string"), ("url", "string"));
app.Tool("remove_calendar", "visor のカレンダーから、読むカレンダーを名前で外す。", a =>
{
    var name = a["name"]?.GetValue<string>() ?? "";
    lock (gate)
    {
        if (sources.RemoveAll(s => s.Name == name) == 0) return "その名前のカレンダーは無い";
        SaveSources();
    }
    _ = Refresh();
    return $"{name} を外した";
}, ("name", "string"));
app.PublishTools();
Draw();

// 15 分おきに読み直し、30 秒おきに知らせるものを見る
_ = Task.Run(async () =>
{
    while (true)
    {
        if (DateTime.UtcNow >= nextRefresh) await Refresh();
        CheckReminders();
        await Task.Delay(TimeSpan.FromSeconds(30));
    }
});

await app.Run();

List<Source> LoadSources()
{
    try
    {
        // 前の visor（カレンダーが visor の中にあったころ）の設定を引き継ぐ
        if (!File.Exists(path) && VisorApp.VisorDataDir is { } old && File.Exists(Path.Combine(old, "calendars.json")))
        {
            Directory.CreateDirectory(VisorApp.DataDir);
            File.Copy(Path.Combine(old, "calendars.json"), path);
        }
        if (File.Exists(path)) return JsonSerializer.Deserialize<List<Source>>(File.ReadAllText(path)) ?? [];
    }
    catch (Exception ex) when (ex is JsonException or IOException)
    {
        Console.Error.WriteLine($"calendars.json を読めなかった: {ex.Message}");
    }
    return [];
}

void SaveSources()
{
    Directory.CreateDirectory(VisorApp.DataDir);
    File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(sources, new JsonSerializerOptions { WriteIndented = true }));
    File.Move(path + ".tmp", path, overwrite: true);
}

async Task Refresh()
{
    List<Source> list;
    lock (gate)
    {
        nextRefresh = DateTime.UtcNow + refreshEvery;
        if (loading) return;
        loading = true;
        list = sources.ToList();
    }
    Draw();
    var found = new List<Item>();
    var failed = new List<string>();
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    foreach (var src in list)
    {
        try
        {
            found.AddRange(Parse(src.Name, await http.GetStringAsync(src.Url)));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"カレンダー {src.Name} を読めなかった: {ex.Message}");
            failed.Add(src.Name);
        }
    }
    lock (gate)
    {
        loading = false;
        items = found.OrderBy(i => i.Start).ToList();
        errors = failed;
        loadedAt = DateTime.Now;
    }
    Draw();
    CheckReminders();
}

// iCal の文から、昨日から 14 日先までの予定を取り出す（繰り返しも広げる）
static IEnumerable<Item> Parse(string calendar, string ics)
{
    var cal = Ical.Net.Calendar.Load(ics);
    if (cal == null) yield break;
    var from = DateTime.UtcNow.Date.AddDays(-1);
    var until = DateTime.UtcNow.Date.AddDays(Days + 1);
    foreach (var o in cal.GetOccurrences(new CalDateTime(from, "UTC", true), new Ical.Net.Evaluation.EvaluationOptions()))
    {
        var start = o.Period.StartTime;
        if (start.AsUtc >= until) break;
        if (o.Source is not Ical.Net.CalendarComponents.CalendarEvent e) continue;
        var end = o.Period.EffectiveEndTime ?? start;
        var allDay = !start.HasTime;
        var s = allDay ? start.Value : start.AsUtc.ToLocalTime();
        var en = allDay ? end.Value : end.AsUtc.ToLocalTime();
        if (en < DateTime.Now.Date) continue;
        yield return new Item(calendar, e.Summary ?? "（題なし）", e.Location, s, en, allDay);
    }
}

void Draw()
{
    JsonObject ui;
    string text;
    lock (gate)
    {
        ui = Build();
        text = Agenda(Days);
    }
    app.Render(ui);
    app.Describe(text);
}

JsonObject Build()
{
    var status = loading ? "読んでいます"
        : errors.Count > 0 ? $"読めなかった: {string.Join("、", errors)}"
        : loadedAt is { } at && sources.Count > 0 ? $"{at:H:mm} に読んだ（{string.Join("・", sources.Select(s => s.Name))}）" : "";
    var head = Ui.Row(Ui.Text(status, "small", "dim").Grow().Align("center"), Ui.Button("refresh", icon: "refresh").Align("center"));
    var list = Ui.Column().Gap(10);
    var rows = (JsonArray)list["children"]!;
    if (sources.Count == 0)
    {
        rows.Add(Ui.Text("読むカレンダーがまだ無い。Google カレンダーなら「設定 → 各カレンダーの設定 → カレンダーの統合 → iCal 形式の非公開 URL」を、" +
            "Claude に「このカレンダーを visor に足して」と渡す", "small", "dim"));
        return Ui.Column(head, list).Gap(14);
    }
    var today = DateTime.Now.Date;
    foreach (var day in Enumerable.Range(0, Days).Select(i => today.AddDays(i)))
    {
        var todays = items.Where(i => i.AllDay ? i.Start.Date <= day && day < i.End.Date || i.Start.Date == day : i.Start.Date == day).ToList();
        if (todays.Count == 0 && day > today.AddDays(1)) continue;
        var rel = day == today ? "今日" : day == today.AddDays(1) ? "明日" : "";
        rows.Add(Ui.Text($"{day.Month}月{day.Day}日（{week[(int)day.DayOfWeek]}）{rel}", color: day == today ? "accent" : null, bold: true));
        if (todays.Count == 0)
        {
            rows.Add(Ui.Text("予定なし", "small", "faint"));
            continue;
        }
        foreach (var i in todays)
        {
            var sub = string.Join("・", new[] { i.Location, sources.Count > 1 ? i.Calendar : null }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var text = Ui.Column(Ui.Text(i.Title)).Gap(2).Grow().Align("center");
            if (sub.Length > 0) ((JsonArray)text["children"]!).Add(Ui.Text(sub, "small", "faint"));
            rows.Add(Ui.Row(
                Ui.Text(i.AllDay ? "終日" : $"{i.Start:H:mm}–{i.End:H:mm}", "small", "dim").Width(170).Align("center"),
                text).Gap(16).Pad(18, 10).Background("card"));
        }
    }
    return Ui.Column(head, Ui.Scroll(list).Grow()).Gap(14).Grow();
}

// 始まる 10 分前（と、始まったばかりのもの）を一度だけ知らせる
void CheckReminders()
{
    var now = DateTime.Now;
    List<Item> soon;
    lock (gate) soon = items.Where(i => !i.AllDay && i.Start >= now.AddMinutes(-1) && i.Start <= now + remindBefore).ToList();
    foreach (var i in soon)
    {
        if (!reminded.Add($"{i.Calendar}|{i.Title}|{i.Start:O}")) continue;
        var mins = (int)Math.Round((i.Start - now).TotalMinutes);
        app.Notify(i.Title, mins > 0 ? $"{mins} 分後（{i.Start:H:mm}）{(i.Location != null ? "・" + i.Location : "")}" : $"始まる（{i.Start:H:mm}）");
    }
}

string Agenda(int days)
{
    if (sources.Count == 0) return "読むカレンダーがまだ無い（add_calendar で足す）";
    var sb = new StringBuilder();
    var until = DateTime.Now.Date.AddDays(days);
    foreach (var i in items.Where(i => i.Start < until))
        sb.AppendLine($"- {i.Start.Month}/{i.Start.Day}（{week[(int)i.Start.DayOfWeek]}） {(i.AllDay ? "終日" : $"{i.Start:H:mm}–{i.End:H:mm}")} {i.Title}{(i.Location != null ? "（" + i.Location + "）" : "")}");
    return sb.Length > 0 ? sb.ToString() : "予定なし";
}

record Source(string Name, string Url);

record Item(string Calendar, string Title, string? Location, DateTime Start, DateTime End, bool AllDay);
