// andEmuera: Android 側のエントリポイント。
//
// やることは 3 つだけ:
//   1. 端末のフォントを Emuera.Core に教える (Android に Windows のフォントは無い)
//   2. ゲームフォルダを探して EmueraWebHost を起動する
//   3. 全画面の WebView をローカルサーバーに繋ぐ
// 表示と入力の処理は WebHost 側 (PC のブラウザと同じもの) がそのまま担当する。

using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using MinorShift.Emuera.WebHost;
using SkiaSharp;
using System;
using System.IO;
using System.Linq;

namespace AndEmuera;

[Activity(
	Label = "@string/app_name",
	MainLauncher = true,
	Theme = "@android:style/Theme.Material.NoActionBar",
	ScreenOrientation = ScreenOrientation.Portrait,
	WindowSoftInputMode = SoftInput.AdjustResize,
	ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.KeyboardHidden)]
public class MainActivity : Activity
{
	const string LogTag = "andEmuera";

	WebView webView;
	TextView statusView;
	EmueraWebHost host;

	protected override void OnCreate(Bundle savedInstanceState)
	{
		base.OnCreate(savedInstanceState);

		// 起動中や失敗時のメッセージを出すための簡易ビュー
		var layout = new FrameLayout(this);
		rootLayout = layout;
		statusView = new TextView(this)
		{
			Text = "起動しています…",
			TextSize = 16,
		};
		statusView.SetPadding(32, 64, 32, 32);
		statusView.SetTextColor(Android.Graphics.Color.LightGray);

		webView = new WebView(this);
		webView.Settings.JavaScriptEnabled = true;
		webView.Settings.DomStorageEnabled = true;
		webView.Settings.MediaPlaybackRequiresUserGesture = false;
		// 拡大はページ側 (index.html) で実装している。WebView 標準のページズームは
		// 操作バーごと拡大されるうえ、1 本指のパンが効かないので使わない
		webView.Settings.BuiltInZoomControls = false;
		webView.Settings.DisplayZoomControls = false;
		webView.Settings.SetSupportZoom(false);
		webView.Settings.UseWideViewPort = true;
		webView.SetBackgroundColor(Android.Graphics.Color.Black);
		webView.SetWebViewClient(new WebViewClient());
		webView.Visibility = ViewStates.Gone;

		layout.AddView(webView);
		layout.AddView(statusView);
		layout.SetBackgroundColor(Android.Graphics.Color.Black);
		SetContentView(layout);
		ApplySystemBarInsets(layout);

		// 画面が消えないようにする (era は読む時間が長い)
		Window?.AddFlags(WindowManagerFlags.KeepScreenOn);

		SetupFonts();

		// CALLSHARP LAUNCH_BROWSER の内蔵実装から呼ばれる
		MinorShift.Emuera.Runtime.Utils.PluginSystem.BuiltinPlugins.BrowserLauncher = OpenUrl;

		var games = FindGames(out string message);
		if (games.Count == 0)
		{
			statusView.Text = message;
			return;
		}

		// 1 つしか入っていないなら選ばせる意味がないのでそのまま起動する
		if (games.Count == 1)
		{
			_ = StartGameAsync(games[0]);
			return;
		}

		ShowSelector(games);
	}

	/// <summary>games フォルダに入っているバリアント。</summary>
	sealed class GameEntry
	{
		public string Name { get; init; }
		public string Path { get; init; }
		public string ErbDir { get; init; }

		/// <summary>選択画面に出す ERB 本数。数え終わるまでは -1。</summary>
		public int ErbCount { get; set; } = -1;

		public override string ToString() => Name;
	}

	FrameLayout rootLayout;
	ListView selectorView;

	/// <summary>バリアントが複数あるときの選択画面。</summary>
	void ShowSelector(System.Collections.Generic.List<GameEntry> games)
	{
		// 前回遊んだものを先頭に出す
		string last = GetSharedPreferences("andemuera", Android.Content.FileCreationMode.Private)
			?.GetString("lastGame", null);
		if (last != null)
		{
			int idx = games.FindIndex(g => g.Name == last);
			if (idx > 0)
			{
				var entry = games[idx];
				games.RemoveAt(idx);
				games.Insert(0, entry);
			}
		}

		string Label(GameEntry g)
		{
			string count = g.ErbCount < 0 ? "ERB 数えています…" : $"ERB {g.ErbCount} 本";
			return g.Name == last
				? $"{g.Name}\n    {count} ・ 前回遊んだもの"
				: $"{g.Name}\n    {count}";
		}

		var labels = games.Select(Label).ToArray();

		statusView.Text = "遊ぶバリアントを選んでください";
		statusView.SetPadding(32, 48, 32, 24);

		selectorView = new ListView(this)
		{
			LayoutParameters = new FrameLayout.LayoutParams(
				ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent),
		};
		selectorView.SetPadding(16, 140, 16, 16);
		selectorView.Adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleListItem1, labels);
		selectorView.ItemClick += (s, e) => _ = StartGameAsync(games[e.Position]);

		rootLayout.AddView(selectorView);

		// ERB 本数は見分けの手がかりでしかないので、一覧を出してから裏で数える
		_ = System.Threading.Tasks.Task.Run(() =>
		{
			foreach (var g in games)
			{
				try { g.ErbCount = Directory.GetFiles(g.ErbDir, "*.ERB", CaseInsensitiveAll).Length; }
				catch { g.ErbCount = 0; }
			}
			RunOnUiThread(() =>
			{
				if (selectorView == null)
					return;
				selectorView.Adapter = new ArrayAdapter<string>(this,
					Android.Resource.Layout.SimpleListItem1, games.Select(Label).ToArray());
			});
		});
	}

	async System.Threading.Tasks.Task StartGameAsync(GameEntry game)
	{
		if (selectorView != null)
		{
			selectorView.Visibility = ViewStates.Gone;
			selectorView = null;
		}

		GetSharedPreferences("andemuera", Android.Content.FileCreationMode.Private)
			?.Edit()?.PutString("lastGame", game.Name)?.Apply();

		try
		{
			var metrics = Resources.DisplayMetrics;
			host = new EmueraWebHost(game.Path, metrics.WidthPixels, metrics.HeightPixels)
			{
				Log = msg => Android.Util.Log.Info(LogTag, msg),
			};
			statusView.Visibility = ViewStates.Visible;
			statusView.Text = $"読み込み中…\n{game.Name}";
			await host.StartAsync();

			statusView.Visibility = ViewStates.Gone;
			webView.Visibility = ViewStates.Visible;
			webView.LoadUrl(host.Url);
		}
		catch (Exception ex)
		{
			Android.Util.Log.Error(LogTag, ex.ToString());
			statusView.Visibility = ViewStates.Visible;
			statusView.Text = $"起動に失敗しました\n\n{ex.GetType().Name}: {ex.Message}";
		}
	}

	/// <summary>
	/// ゲームから要求された URL を既定のブラウザで開く。
	/// スクリプトが指定した任意の URL なので、開く前に確認を挟む。
	/// </summary>
	void OpenUrl(string url)
	{
		RunOnUiThread(() =>
		{
			new AlertDialog.Builder(this)
				.SetTitle("リンクを開きますか?")
				.SetMessage(url)
				.SetPositiveButton("開く", (s, e) =>
				{
					try
					{
						var intent = new Android.Content.Intent(Android.Content.Intent.ActionView,
							Android.Net.Uri.Parse(url));
						intent.AddFlags(Android.Content.ActivityFlags.NewTask);
						StartActivity(intent);
					}
					catch (Exception ex)
					{
						Android.Util.Log.Error(LogTag, $"URL を開けません: {ex.Message}");
					}
				})
				.SetNegativeButton("キャンセル", (s, e) => { })
				.Show();
		});
	}

	/// <summary>
	/// Android 15 以降は画面がシステムバーの裏まで広がるため、
	/// ステータスバー・ナビゲーションバー・キーボードの分だけ余白を入れて重なりを防ぐ。
	/// </summary>
	void ApplySystemBarInsets(View root)
	{
		if (!OperatingSystem.IsAndroidVersionAtLeast(30))
			return;

		root.SetOnApplyWindowInsetsListener(new InsetListener());
		root.RequestApplyInsets();
	}

	sealed class InsetListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
	{
		public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
		{
			var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.Ime());
			v.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
			return insets;
		}
	}

	/// <summary>APK に同梱している等幅フォント (Assets/fonts/)。</summary>
	const string BundledFontAsset = "fonts/BIZUDGothic-Regular.ttf";

	/// <summary>
	/// Emuera はフォント名 (既定では emuera.config の指定) でフォントを探すが、
	/// Android には Windows のフォントが無い。
	///
	/// 名前で引けなかったときの受け皿を「共有 fonts/ → 同梱フォント → 端末のフォント」の順で決める。
	/// ゲーム同梱の font/ は <see cref="MinorShift.Emuera.Program.LoadGameFonts"/> が
	/// 後から登録するので、そちらが優先される。
	///
	/// 端末には等幅の CJK フォントが基本的に入っていない。等幅でないと PRINTC の桁揃えが
	/// 崩れる (半角スペースが半角文字より狭く、詰めたぶんを全部剥がされる) ため、
	/// 同梱フォントまでで決まるのが正常で、端末フォントに落ちるのはあくまで妥協。
	/// </summary>
	void SetupFonts()
	{
		// 同梱フォントは名前でも引けるように必ず登録する。
		// emuera.config が BIZ UDGothic を指しているゲームは、これで指定どおりに描ける
		SKTypeface bundled = LoadBundledFont();

		// 全ゲームで使い回す置き場。同梱フォントより後に登録することで、
		// 同じ名前なら利用者が置いたほうが勝つ (差し替え用)
		SKTypeface shared = null;
		string sharedDir = SharedFontDir();
		if (sharedDir != null)
		{
			int loaded = System.Drawing.FontResolver.RegisterDirectory(sharedDir, out shared);
			if (loaded > 0)
				Android.Util.Log.Info(LogTag, $"共有フォント: {loaded} 件 ({sharedDir}) → 既定 {shared?.FamilyName}");
		}

		var fallback = PickFallback(shared, bundled);

		System.Drawing.FontResolver.Fallback = fallback;
		Android.Util.Log.Info(LogTag, $"フォールバックフォント: {fallback.FamilyName}");

		// ゲームのフォントに無い記号 (✕ ❤ 简体字 など) をどのフォントで補ったか。
		// 1 コードポイントにつき 1 回だけ出る
		System.Drawing.GlyphFallback.Log = message => Android.Util.Log.Info(LogTag, message);
	}

	/// <summary>
	/// 名前で引けなかったときの受け皿を選ぶ。候補は「利用者が置いた共有 fonts/ → APK 同梱 → 端末」の順だが、
	/// <b>等幅でないものは飛ばす</b>。
	///
	/// ここが効くのは <c>font/</c> を同梱せず <c>ＭＳ ゴシック</c> のような Windows のフォント名を
	/// 指定しているゲーム (eraTOWN ほか大半のバリアント)。名前では絶対に引けないので、
	/// <b>この受け皿がそのまま本文フォントになる</b>。比例フォントを据えると
	/// PRINTC のパディングが 1 つ残らず剥がされ、選択肢が横一列に繋がる
	/// (docs/porting-notes.md「PRINTC の桁揃えは等幅フォント前提」)。
	///
	/// 「共有 fonts/ に置いたものが勝つ」という差し替えの仕様は保ったまま、
	/// そこへ比例フォントを置いても桁揃えだけは壊れないようにするのが狙い。
	/// </summary>
	SKTypeface PickFallback(SKTypeface shared, SKTypeface bundled)
	{
		// 端末フォントの探索は名前を順に引くので、そこまで落ちたときだけ走らせる
		(string Source, Func<SKTypeface> Get)[] candidates =
		[
			("共有 fonts/", () => shared),
			("APK 同梱", () => bundled),
			("端末", FindDeviceFont),
		];

		SKTypeface first = null;
		foreach (var (source, get) in candidates)
		{
			var face = get();
			if (face == null)
				continue;
			first ??= face;
			if (System.Drawing.FontMetrics.IsMonospaced(face))
			{
				Android.Util.Log.Info(LogTag, $"受け皿に採用: {face.FamilyName} ({source}・等幅)");
				return face;
			}
			Android.Util.Log.Info(LogTag,
				$"受け皿の候補を見送り: {face.FamilyName} ({source}) — 等幅でないため PRINTC の桁が揃わない");
		}

		// 等幅が 1 つも無い端末。読めることを優先して先頭の候補に戻す。
		// この場合は EmueraEngine.CheckMonospaced が同じ基準で気づいて画面に警告を出す
		var any = first ?? SKTypeface.Default;
		Android.Util.Log.Warn(LogTag,
			$"等幅フォントが見つかりません。{any.FamilyName} で描きます (選択肢の桁揃えが崩れます)");
		return any;
	}

	/// <summary>
	/// APK に同梱した等幅フォントを読む。これがあるおかげで、受け取った人が
	/// フォントを別途端末へ送らなくても選択肢の桁揃えが崩れない。
	///
	/// asset のストリームは seek できないので、いったんメモリへ写す。
	/// 写したものは <see cref="SKData"/> に持たせる — 管理ストリームのまま渡すと、
	/// Skia が遅延読みする間こちらが生かし続けなければならない。
	/// </summary>
	SKTypeface LoadBundledFont()
	{
		try
		{
			using var asset = Assets.Open(BundledFontAsset);
			using var buffer = new MemoryStream();
			asset.CopyTo(buffer);

			using var data = SKData.CreateCopy(buffer.ToArray());
			var typeface = SKTypeface.FromData(data);
			if (typeface == null)
			{
				Android.Util.Log.Warn(LogTag, $"同梱フォントを読めません: {BundledFontAsset}");
				return null;
			}

			// emuera.config のフォント名がこれと一致するゲームは、直接このフェイスで描かれる
			System.Drawing.FontResolver.Register(typeface.FamilyName, typeface);
			Android.Util.Log.Info(LogTag, $"同梱フォント: {typeface.FamilyName}");
			return typeface;
		}
		catch (Exception ex)
		{
			Android.Util.Log.Warn(LogTag, $"同梱フォントを読めません: {ex.Message}");
			return null;
		}
	}

	/// <summary>端末に入っている日本語対応フォントを拾う。等幅を優先する。</summary>
	static SKTypeface FindDeviceFont()
	{
		foreach (var name in new[] { "Noto Sans Mono CJK JP", "Droid Sans Mono", "monospace", "Noto Sans CJK JP", "sans-serif" })
		{
			var face = SKTypeface.FromFamilyName(name);
			if (face != null && face.ContainsGlyphs("あ漢"))
				return face;
		}

		// それでも見つからなければ、日本語のグリフを持つフォントを Skia に探させる
		return SKFontManager.Default.MatchCharacter('あ') ?? SKTypeface.Default;
	}

	/// <summary>games/ の隣に置く共有フォントフォルダ。無ければ作って場所を示す。</summary>
	string SharedFontDir()
	{
		string baseDir = GetExternalFilesDir(null)?.AbsolutePath;
		if (baseDir == null)
			return null;
		string dir = Path.Combine(baseDir, "fonts");
		try { Directory.CreateDirectory(dir); }
		catch { return null; }
		return dir;
	}

	/// <summary>
	/// 入っているバリアントを列挙する。 /sdcard/Android/data/&lt;pkg&gt;/files/games/&lt;タイトル&gt;/ を見て、
	/// csv と erb を両方持つフォルダを候補とする。
	/// </summary>
	System.Collections.Generic.List<GameEntry> FindGames(out string message)
	{
		var result = new System.Collections.Generic.List<GameEntry>();

		string baseDir = GetExternalFilesDir(null)?.AbsolutePath;
		if (baseDir == null)
		{
			message = "外部ストレージを利用できません。";
			return result;
		}

		string gamesDir = Path.Combine(baseDir, "games");
		Directory.CreateDirectory(gamesDir);

		foreach (var dir in Directory.GetDirectories(gamesDir).OrderBy(d => d))
		{
			// フォルダ名が CSV / ERB と大文字のこともある。Android は区別するので非依存で探す
			string erbDir = FindSubDir(dir, "erb");
			if (FindSubDir(dir, "csv") == null || erbDir == null)
				continue;

			// ERB 本数はここでは数えない。erb/ 配下の再帰列挙は実データで
			// 2,867 ファイル / 655 フォルダになり、OnCreate を同期で止めてしまう
			// (その間は「起動しています…」すら描画されない)。
			// 使い道は選択画面の表示だけなので、選択画面を出すときに裏で数える
			result.Add(new GameEntry
			{
				Name = Path.GetFileName(dir),
				Path = dir,
				ErbDir = erbDir,
			});
		}

		message = result.Count > 0 ? null :
			"ゲームデータが見つかりません。\n\n" +
			"次のフォルダに、csv と erb を含むゲームフォルダを置いてください:\n\n" +
			gamesDir + "/<ゲーム名>/\n\n" +
			"PC から転送する例:\n" +
			"adb push erablue_resort " + gamesDir + "/";
		return result;
	}

	static readonly EnumerationOptions CaseInsensitiveAll = new()
	{
		MatchCasing = MatchCasing.CaseInsensitive,
		RecurseSubdirectories = true,
		IgnoreInaccessible = true,
	};

	static readonly EnumerationOptions CaseInsensitiveTop = new()
	{
		MatchCasing = MatchCasing.CaseInsensitive,
		RecurseSubdirectories = false,
		IgnoreInaccessible = true,
	};

	/// <summary>大文字小文字を区別せずにサブフォルダを探す。無ければ null。</summary>
	static string FindSubDir(string parent, string name)
	{
		string exact = Path.Combine(parent, name);
		if (Directory.Exists(exact))
			return exact;
		foreach (var dir in Directory.EnumerateDirectories(parent, name, CaseInsensitiveTop))
			return dir;
		return null;
	}

	public override void OnBackPressed()
	{
		// 誤操作で終了しないよう、バックキーはホームへ戻す扱いにする
		MoveTaskToBack(true);
	}

	protected override void OnDestroy()
	{
		host?.Dispose();
		base.OnDestroy();
	}
}
