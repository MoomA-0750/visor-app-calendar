# visor-app-calendar

[visor](https://github.com/MoomA-0750/visor)（VR の中で Claude と話すための SteamVR のオーバーレイ）のアプリ「カレンダー」。

- iCal 形式の URL（Google カレンダーなら「設定 → 各カレンダーの設定 → カレンダーの統合 → iCal 形式の非公開 URL」）を読み、今日から 14 日分の予定を並べる。Claude を通さずに動く
- 始まる 10 分前に visor の通知を出す（visor を起こしたときから動くので、窓を開いていなくても届く）
- Claude に道具 `list_events`・`add_calendar`・`remove_calendar` を渡す。「このカレンダーを visor に足して」と URL を渡せば足せる
- 読むカレンダーは `%LOCALAPPDATA%\visor\appdata\calendar\calendars.json`

## 入れ方

visor のホームの「アプリを探す」から入れる（visor の公式のカタログに載っている）。

## 作り方

- .NET 9 の C#。iCal は Ical.Net。visor との受け渡しは `VisorApp.cs`（visor の `sdk/dotnet/VisorApp.cs` の写し）
- `dotnet publish -c Release -o bin` で組み立てる。visor はリリースに `visor-app.zip` があればそれを、無ければソースを取って `app.json` の `build` で組み立てる
- `v0.1.0` のようなタグを push すると、GitHub Actions がリリースを作る
