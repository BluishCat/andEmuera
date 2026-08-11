// andEmuera: 移植したコアが実データを読めるかを PC 上で確認するための CLI。
//
// 使い方:  Emuera.TestHarness <ゲームフォルダ> [--input <文字列>]...
// ゲームフォルダには csv / erb / emuera.config が必要。
// Windows 版 Emuera と同じ警告が出るかを emuera.log で突き合わせるために使う。

using MinorShift.Emuera.Api;
using MinorShift.Emuera.Forms;
using MinorShift.Emuera.Runtime.Utils;
using System.Diagnostics;

namespace Emuera.TestHarness;

/// <summary>
/// 画面側の受け口。CLI では通知を数えるだけで、描画は行わない。
/// </summary>
sealed class ConsoleHost : IWindowHost
{
	public int RedrawCount { get; private set; }
	public string Title { get; private set; } = "";
	public string InputText { get; private set; } = "";
	public string LastToolTip { get; private set; }
	public bool CloseRequested { get; private set; }
	public bool RebootRequested { get; private set; }

	public void RequestRedraw() => RedrawCount++;
	public void SetTitle(string title) => Title = title;
	public void SetInputText(string text) => InputText = text;
	public void SetInputPosition(int xOffset, int yOffset, int width) { }
	public void ResetInputPosition() { }
	public void ShowToolTip(string text, int x, int y) => LastToolTip = text;
	public void RequestClose() => CloseRequested = true;
	public void RequestReboot() => RebootRequested = true;
}

static class Harness
{
	static async Task<int> Main(string[] args)
	{
		Console.OutputEncoding = System.Text.Encoding.UTF8;

		if (args.Length == 0)
		{
			Console.Error.WriteLine("使い方: Emuera.TestHarness <ゲームフォルダ> [--input <文字列>] [--scroll <行数>] [--size 1600x2691]...");
			Console.Error.WriteLine("        Emuera.TestHarness --selftest-path [ゲームフォルダ]");
			Console.Error.WriteLine("        Emuera.TestHarness --selftest-scroll");
			Console.Error.WriteLine("        Emuera.TestHarness --selftest-draw");
			Console.Error.WriteLine("        Emuera.TestHarness --selftest-font <ゲームフォルダ> [--font-fallback <フォント.ttf>]");
			Console.Error.WriteLine("        Emuera.TestHarness --selftest-glyph <ゲームフォルダ>");
			Console.Error.WriteLine("        Emuera.TestHarness --verify-encoders <ゲームフォルダ> [--input <文字列>]...");
			Console.Error.WriteLine("        Emuera.TestHarness --bench <ゲームフォルダ> [--input <文字列>]...");
			Console.Error.WriteLine("        Emuera.TestHarness <ゲームフォルダ> --capture <出力フォルダ> [--input <文字列>]...");
			Console.Error.WriteLine("        Emuera.TestHarness --compare <a.png> <b.png>");
			return 1;
		}

		if (args[0] == "--selftest-path")
			return SelfTestPath(args.Length > 1 ? args[1] : null);

		if (args[0] == "--selftest-scroll")
			return SelfTestScroll();

		if (args[0] == "--selftest-draw")
			return SelfTestDraw();

		if (args[0] == "--selftest-font")
		{
			if (args.Length < 2)
			{
				Console.Error.WriteLine("使い方: Emuera.TestHarness --selftest-font <ゲームフォルダ> [--font-fallback <フォント.ttf>]");
				return 1;
			}
			int fallbackAt = Array.IndexOf(args, "--font-fallback");
			return SelfTestFont(args[1], fallbackAt >= 0 && fallbackAt + 1 < args.Length ? args[fallbackAt + 1] : null);
		}

		if (args[0] == "--selftest-glyph")
		{
			if (args.Length < 2)
			{
				Console.Error.WriteLine("使い方: Emuera.TestHarness --selftest-glyph <ゲームフォルダ> [--shot <出力.png>]");
				return 1;
			}
			int shotAt = Array.IndexOf(args, "--shot");
			return SelfTestGlyph(args[1], shotAt >= 0 && shotAt + 1 < args.Length ? args[shotAt + 1] : null);
		}

		if (args[0] == "--compare")
		{
			if (args.Length < 3)
			{
				Console.Error.WriteLine("使い方: Emuera.TestHarness --compare <a.png> <b.png>");
				return 1;
			}
			return ComparePng(args[1], args[2]);
		}

		bool verifyEncoders = args[0] == "--verify-encoders";
		bool bench = args[0] == "--bench";
		if ((verifyEncoders || bench) && args.Length < 2)
		{
			Console.Error.WriteLine($"使い方: Emuera.TestHarness {args[0]} <ゲームフォルダ>");
			return 1;
		}

		string gameDir = verifyEncoders || bench ? args[1] : args[0];
		// 操作の並び。--input は入力の確定、--scroll はバックログ送り (正で過去へ)
		var actions = new List<(bool Scroll, string Value)>();
		string shotPath = null;
		string captureDir = null;
		int dumpLines = 0;      // --dump N で最後の N 行を文字で出す
		bool serve = false;
		int port = 8321;
		// 描画領域。既定は PC 相当。端末は縦長で画素が 2 倍以上あり、
		// 描画コストの比重が変わるので --size 1600x2691 のように渡して再現する
		int width = 1600, height = 1120;
		for (int i = verifyEncoders || bench ? 2 : 1; i < args.Length; i++)
		{
			if (args[i] == "--input" && i + 1 < args.Length)
				actions.Add((false, args[++i]));
			else if (args[i] == "--scroll" && i + 1 < args.Length)
				actions.Add((true, args[++i]));
			else if (args[i] == "--shot" && i + 1 < args.Length)
				shotPath = args[++i];
			else if (args[i] == "--capture" && i + 1 < args.Length)
				captureDir = args[++i];
			else if (args[i] == "--dump" && i + 1 < args.Length)
				dumpLines = int.Parse(args[++i]);
			else if (args[i] == "--serve")
				serve = true;
			else if (args[i] == "--port" && i + 1 < args.Length)
				port = int.Parse(args[++i]);
			else if (args[i] == "--size" && i + 1 < args.Length)
			{
				var wh = args[++i].Split('x', 'X');
				if (wh.Length != 2 || !int.TryParse(wh[0], out width) || !int.TryParse(wh[1], out height))
				{
					Console.Error.WriteLine("--size は 1600x2691 の形で指定してください");
					return 1;
				}
			}
		}

		if (serve)
			return await ServeAsync(gameDir, port);

		if (!Directory.Exists(gameDir))
		{
			Console.Error.WriteLine($"フォルダがありません: {gameDir}");
			return 1;
		}

		Console.WriteLine($"ゲームフォルダ: {Path.GetFullPath(gameDir)}");
		Console.WriteLine($"ERB: {CountFiles(Path.Combine(gameDir, "erb"), "*.ERB")} 本 / " +
						  $"CSV: {CountFiles(Path.Combine(gameDir, "csv"), "*.csv")} 本");

		var host = new ConsoleHost();
		var sw = Stopwatch.StartNew();
		EmueraEngine engine;
		try
		{
			engine = await EmueraEngine.StartAsync(gameDir, host, width, height);
		}
		catch (Exception ex)
		{
			sw.Stop();
			Console.Error.WriteLine($"起動に失敗 ({sw.ElapsedMilliseconds}ms): {ex.GetType().Name}: {ex.Message}");
			Console.Error.WriteLine(ex.StackTrace);
			return 2;
		}
		sw.Stop();

		using (engine)
		{
			Console.WriteLine($"ロード完了: {sw.ElapsedMilliseconds}ms  IsError={engine.IsError}  再描画要求={host.RedrawCount}回");

			var capture = captureDir == null ? null : new CaptureWriter(captureDir);
			capture?.Write(engine, "000", "(タイトル)");

			int step = 0;
			int scrollNg = 0;
			foreach (var (isScroll, value) in actions)
			{
				// 上流は表示状態 (EscapedParts) を OnPaint の中で確定し、BINPUT などがそれを参照する。
				// WebHost はメッセージ処理の前に必ず描いてから渡すので、ここでも同じ手順を踏む
				engine.EnsureRendered();
				// 「処理中」の実体を測る。スクリプトは同期実行なので、この区間が
				// そのまま端末で待たされる時間になる (エンコードと転送はこの外側)
				long paintsBefore = engine.PaintCount;
				double paintMsBefore = engine.PaintMs;
				long measureCallsBefore = System.Drawing.GlyphFallback.MeasureCalls;
				long measureCharsBefore = System.Drawing.GlyphFallback.MeasureChars;
				double measureMsBefore = System.Drawing.GlyphFallback.MeasureMs;
				var work = Stopwatch.StartNew();
				if (isScroll)
				{
					int lines = int.Parse(value);
					Console.WriteLine($"[スクロール] {lines} 行{(lines > 0 ? " (過去へ)" : " (最新へ)")}");
					engine.ScrollLines(lines);
				}
				else
				{
					Console.WriteLine($"[入力] {value}");
					engine.SubmitInput(value);
				}
				// 実行側が入力を処理し終わるのを待つ
				for (int i = 0; i < 100 && engine.IsInProcess; i++)
					await Task.Delay(100);
				double totalMs = work.Elapsed.TotalMilliseconds;
				long paints = engine.PaintCount - paintsBefore;
				double paintMs = engine.PaintMs - paintMsBefore;
				var scroll = engine.ScrollState;
				Console.WriteLine($"  → IsError={engine.IsError} IsInProcess={engine.IsInProcess} " +
								  $"スクロール={scroll.Value}/{scroll.Max}{(scroll.Value > scroll.Max ? "  ★最下行より下を指している" : "")}");
				Console.WriteLine($"  → 処理 {totalMs:0.0}ms = スクリプト {totalMs - paintMs:0.0}ms + " +
								  $"フル描画 {paints} 回 {paintMs:0.0}ms" +
								  $"{(paints > 1 ? $" (1 回あたり {paintMs / paints:0.0}ms)" : "")}");
				// ANDEMUERA_MEASURE_PROFILE=1 のときだけ中身が入る
				long calls = System.Drawing.GlyphFallback.MeasureCalls - measureCallsBefore;
				if (calls > 0)
					Console.WriteLine($"     うち文字幅の計測 {calls} 回 " +
									  $"{System.Drawing.GlyphFallback.MeasureMs - measureMsBefore:0.0}ms " +
									  $"({System.Drawing.GlyphFallback.MeasureChars - measureCharsBefore} 文字)");
				// 最下行を表示しているなら Value == Max。Value が Max を超えるのは
				// 表示行が減ったのに位置を締め直せていない状態で、画面が上へずれる
				if (scroll.Value > scroll.Max)
					scrollNg++;
				capture?.Write(engine, (++step).ToString("000"), isScroll ? $"scroll {value}" : value);
			}
			if (scrollNg > 0)
				Console.WriteLine($"NG  スクロール位置が最大値を超えた回数: {scrollNg}");

			capture?.Finish();

			if (dumpLines > 0)
			{
				engine.EnsureRendered();
				var depths = engine.EscapedPartDepths();
				Console.WriteLine("--- 直近の描画パーツ (奥行き:個数) ---");
				Console.WriteLine(depths.Length == 0
					? "  なし"
					: string.Join(" / ", depths.Select(d => $"{d.Depth}:{d.Count}")));
				if (depths.Length > 0 && Array.TrueForAll(depths, d => d.Depth != 0))
					Console.WriteLine("  ★ 奥行き 0 が無い → 上流の OnPaint は通常の行テキストを描かない");
				Console.WriteLine($"描画領域の高さ: {engine.CanvasHeight}");
				foreach (var p in engine.EscapedPartBoxes())
					Console.WriteLine($"  depth={p.Depth,4} {p.Kind,-22} 行={p.Line,4} top={p.Top,5} bottom={p.Bottom,5}");

				var lines = engine.GetDisplayLines();
				int from = Math.Max(0, lines.Length - dumpLines);
				Console.WriteLine($"--- 表示中のログ 末尾 {lines.Length - from} 行 (全 {lines.Length} 行) ---");
				for (int i = from; i < lines.Length; i++)
					Console.WriteLine($"{i,5}| {lines[i]}");
			}

			if (shotPath != null)
			{
				engine.SaveScreenshot(shotPath);
				Console.WriteLine($"画面を保存しました: {shotPath}");
			}

			int extra = 0;
			if (verifyEncoders)
				extra = VerifyEncoders(engine);
			if (bench)
				Bench(engine);

			ReportLog(gameDir);
			if (extra != 0)
				return 4;
			if (scrollNg > 0)
				return 5;
			return engine.IsError ? 3 : 0;
		}
	}

	// --- エンコード設定の検証・計測 ---

	/// <summary>スイープするエンコード設定。既定 (All/6) を先頭に置き、これを基準にする。</summary>
	static readonly (PngFilterMode Filter, int Level)[] EncoderCases =
	[
		(PngFilterMode.All, 6),          // Skia の既定 = 現行
		(PngFilterMode.All, 2),
		(PngFilterMode.Up, 6),
		(PngFilterMode.Up, 3),
		(PngFilterMode.Up, 2),
		(PngFilterMode.Up, 1),
		(PngFilterMode.Sub, 2),
		(PngFilterMode.Sub, 1),
		(PngFilterMode.Paeth, 2),
		(PngFilterMode.None, 6),
		(PngFilterMode.None, 3),
		(PngFilterMode.None, 2),
		(PngFilterMode.None, 1),
		(PngFilterMode.NoFilters, 1),
	];

	/// <summary>
	/// 同じ画面を各設定でエンコードし、デコードし直した画素が基準と完全一致することを確かめる。
	/// PNG は可逆なので必ず一致するはずで、一致しなければ実装ミス。
	/// 表示互換を崩していないことは「バイト列」ではなく「画素」で見る。
	/// </summary>
    static int VerifyEncoders(EmueraEngine engine)
	{
		Console.WriteLine("--- エンコード設定の検証 (画素一致 + 速度) ---");

		var baseline = engine.RenderPng(EncoderCases[0].Filter, EncoderCases[0].Level);
		if (baseline == null)
		{
			Console.WriteLine("NG  画面を取得できません");
			return 1;
		}
		var basePixels = DecodePixels(baseline, out int w, out int h);
		if (basePixels == null)
		{
			Console.WriteLine("NG  基準 PNG をデコードできません");
			return 1;
		}
		Console.WriteLine($"画面サイズ: {w}x{h} ({(long)w * h / 1_000_000.0:0.0} メガピクセル)");
		Console.WriteLine($"α が全面 255: {(IsFullyOpaque(basePixels) ? "はい" : "いいえ")}");

		// 既定の SKImage.Encode(Png, 100) が実際に何を使っているかの裏取り
		var legacy = SaveViaImageFormat(engine);
		if (legacy != null)
			Console.WriteLine($"Save(ImageFormat.Png) は All/6 と{(legacy.AsSpan().SequenceEqual(baseline) ? "同一" : "別物")} " +
							  $"({legacy.Length:N0} bytes / {baseline.Length:N0} bytes)");

		int ng = 0;
		Console.WriteLine($"{"設定",-14}{"ms",8}{"KB",10}{"対 既定",10}  画素");
		foreach (var (filter, level) in EncoderCases)
		{
			// 1 回捨ててから 5 回の中央値を取る (JIT とキャッシュの影響を抜く)
			engine.RenderPng(filter, level);
			var times = new List<double>();
			byte[] png = null;
			for (int i = 0; i < 5; i++)
			{
				var t = Stopwatch.StartNew();
				png = engine.RenderPng(filter, level);
				times.Add(t.Elapsed.TotalMilliseconds);
			}
			times.Sort();
			double ms = times[times.Count / 2];

			var pixels = DecodePixels(png, out int pw, out int ph);
			bool same = pixels != null && pw == w && ph == h && pixels.AsSpan().SequenceEqual(basePixels);
			if (!same)
				ng++;
			Console.WriteLine($"{filter + "/" + level,-14}{ms,8:0.0}{png.Length / 1024.0,10:0}" +
							  $"{(double)png.Length / baseline.Length,10:0.00}  {(same ? "OK" : "NG 不一致")}");
		}

		Console.WriteLine(ng == 0 ? "画素はすべて一致 (可逆)" : $"{ng} 件が基準と一致しません");
		return ng;
	}

	static void Bench(EmueraEngine engine)
	{
		Console.WriteLine("--- 描画単体の計測 ---");
		// EnsureRendered は dirty のときだけ描くので、毎回 dirty にしてから測る
		var times = new List<double>();
		for (int i = 0; i < 20; i++)
		{
			engine.MarkDirty();
			var t = Stopwatch.StartNew();
			engine.EnsureRendered();
			times.Add(t.Elapsed.TotalMilliseconds);
		}
		times.Sort();
		Console.WriteLine($"EnsureRendered (フル描画): 中央値 {times[times.Count / 2]:0.0}ms " +
						  $"最小 {times[0]:0.0}ms 最大 {times[^1]:0.0}ms");

		Console.WriteLine("--- ハッシュの計測 ---");
		times.Clear();
		for (int i = 0; i < 20; i++)
		{
			var t = Stopwatch.StartNew();
			engine.HashBackBuffer();
			times.Add(t.Elapsed.TotalMilliseconds);
		}
		times.Sort();
		Console.WriteLine($"HashBackBuffer: 中央値 {times[times.Count / 2]:0.0}ms");

		VerifyEncoders(engine);
	}

	/// <summary>従来経路 (Image.Save + ImageFormat.Png) の出力。既定設定の裏取り用。</summary>
	static byte[] SaveViaImageFormat(EmueraEngine engine)
	{
		string tmp = Path.Combine(Path.GetTempPath(), $"andemuera-legacy-{Environment.ProcessId}.png");
		try
		{
			engine.SaveScreenshot(tmp);
			return File.Exists(tmp) ? File.ReadAllBytes(tmp) : null;
		}
		catch { return null; }
		finally { try { File.Delete(tmp); } catch { } }
	}

	/// <summary>PNG をデコードして RGBA のバイト列にする。比較はこれで行う。</summary>
	static byte[] DecodePixels(byte[] png, out int width, out int height)
	{
		width = height = 0;
		if (png == null || png.Length == 0)
			return null;
		using var bmp = SkiaSharp.SKBitmap.Decode(png);
		if (bmp == null)
			return null;
		width = bmp.Width;
		height = bmp.Height;
		// カラータイプの差で誤検出しないよう、必ず RGBA8888/Unpremul へ揃える
		var info = new SkiaSharp.SKImageInfo(bmp.Width, bmp.Height,
			SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
		using var src = bmp.PeekPixels();
		if (src == null)
			return null;

		var buffer = new byte[info.BytesSize];
		var handle = System.Runtime.InteropServices.GCHandle.Alloc(
			buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
		try
		{
			if (!src.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes))
				return null;
		}
		finally { handle.Free(); }
		return buffer;
	}

	static bool IsFullyOpaque(byte[] rgba)
	{
		for (int i = 3; i < rgba.Length; i += 4)
			if (rgba[i] != 255)
				return false;
		return true;
	}

	static int ComparePng(string a, string b)
	{
		if (!File.Exists(a) || !File.Exists(b))
		{
			Console.Error.WriteLine("ファイルがありません");
			return 1;
		}
		var pa = DecodePixels(File.ReadAllBytes(a), out int wa, out int ha);
		var pb = DecodePixels(File.ReadAllBytes(b), out int wb, out int hb);
		if (pa == null || pb == null)
		{
			Console.Error.WriteLine("デコードできません");
			return 1;
		}
		if (wa != wb || ha != hb)
		{
			Console.WriteLine($"NG  サイズが違います: {wa}x{ha} と {wb}x{hb}");
			return 1;
		}

		int diff = 0, firstX = -1, firstY = -1;
		for (int i = 0; i < pa.Length; i += 4)
		{
			if (pa[i] == pb[i] && pa[i + 1] == pb[i + 1] && pa[i + 2] == pb[i + 2] && pa[i + 3] == pb[i + 3])
				continue;
			if (diff == 0)
			{
				int px = i / 4;
				firstX = px % wa;
				firstY = px / wa;
			}
			diff++;
		}

		if (diff == 0)
			Console.WriteLine($"OK  画素が完全一致 ({wa}x{ha})");
		else
			Console.WriteLine($"NG  {diff:N0} 画素が違います (最初の差分: x={firstX} y={firstY})");
		return diff == 0 ? 0 : 1;
	}

	/// <summary>
	/// 入力を進めながら各段階の画面を保存し、生ピクセルの SHA-256 を並べる。
	/// 変更の前後で hashes.txt が全行一致すれば表示は完全に同じ。
	/// </summary>
	sealed class CaptureWriter(string dir)
	{
		readonly List<string> lines = [];

		public void Write(EmueraEngine engine, string name, string label)
		{
			Directory.CreateDirectory(dir);
			string path = Path.Combine(dir, name + ".png");
			engine.SaveScreenshot(path);

			var pixels = DecodePixels(File.ReadAllBytes(path), out int w, out int h);
			string hash = pixels == null
				? "(デコード失敗)"
				: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels));
			bool opaque = pixels != null && IsFullyOpaque(pixels);

			lines.Add($"{name}  {w}x{h}  α255={(opaque ? "yes" : "no")}  {hash}  {label}");
			Console.WriteLine($"[capture] {name}.png {w}x{h} {hash[..16]}…");
		}

		public void Finish()
		{
			File.WriteAllLines(Path.Combine(dir, "hashes.txt"), lines);
			Console.WriteLine($"[capture] {lines.Count} 枚 → {Path.Combine(dir, "hashes.txt")}");
		}
	}

	/// <summary>
	/// Android に載せる前に、同じ WebHost を PC のブラウザで動かして確認するためのモード。
	/// </summary>
	static async Task<int> ServeAsync(string gameDir, int port)
	{
		using var host = new MinorShift.Emuera.WebHost.EmueraWebHost(gameDir, 960, 1440, port)
		{
			Log = msg => Console.WriteLine("[host] " + msg),
		};

		try
		{
			await host.StartAsync();
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"起動に失敗: {ex.GetType().Name}: {ex.Message}");
			return 2;
		}

		Console.WriteLine($"ブラウザで開いてください: {host.Url}");
		Console.WriteLine("終了するには Ctrl+C");

		var quit = new TaskCompletionSource();
		Console.CancelKeyPress += (s, e) => { e.Cancel = true; quit.TrySetResult(); };
		await quit.Task;
		return 0;
	}

	/// <summary>
	/// 描画シムのクリップ操作を確かめる。
	///
	/// 上流の ConsoleDivPart.DrawTo は「div の矩形にクリップ → 中身を描く → ResetClip」で、
	/// 戻し忘れると<b>その後に描くものすべてがその矩形に閉じ込められる</b>。
	/// EmueraConsole.OnPaint は奥のパーツ → 行テキスト → 手前のパーツ の順に描くので、
	/// 奥に div がある画面では行テキストが丸ごと消える。
	/// </summary>
	static int SelfTestDraw()
	{
		int ng = 0;

		void Check(string label, bool ok)
		{
			if (!ok)
				ng++;
			Console.WriteLine($"{(ok ? "OK  " : "NG  ")}{label}");
		}

		Console.WriteLine("--- Graphics のクリップ ---");

		using var bmp = new System.Drawing.Bitmap(32, 32);
		using (var g = System.Drawing.Graphics.FromImage(bmp))
		{
			g.Clear(System.Drawing.Color.Black);
			using var white = new System.Drawing.SolidBrush(System.Drawing.Color.White);

			// 左上 8x8 にクリップして全面を塗る → クリップ内だけ白くなる
			g.SetClip(new System.Drawing.Rectangle(0, 0, 8, 8), System.Drawing.Drawing2D.CombineMode.Replace);
			g.FillRectangle(white, 0, 0, 32, 32);
			Check("クリップ内が塗られる", bmp.GetPixel(4, 4).R > 200);
			Check("クリップ外は塗られない", bmp.GetPixel(20, 20).R < 50);

			// 解除してから塗ると、さっきのクリップ外にも描ける
			g.ResetClip();
			g.FillRectangle(white, 16, 16, 8, 8);
			Check("ResetClip 後にクリップ外へ描ける", bmp.GetPixel(20, 20).R > 200);

			// 解除したあとにもう一度クリップを掛け直せる
			g.SetClip(new System.Drawing.Rectangle(0, 24, 8, 8), System.Drawing.Drawing2D.CombineMode.Replace);
			g.FillRectangle(white, 0, 0, 32, 32);
			Check("掛け直したクリップが効く", bmp.GetPixel(4, 28).R > 200 && bmp.GetPixel(28, 4).R < 50);
		}

		Console.WriteLine(ng == 0 ? "すべて OK" : $"NG {ng} 件");
		return ng == 0 ? 0 : 1;
	}

	/// <summary>
	/// スクロールバー互換シムが WinForms と同じ「代入の副作用」を持つかを確かめる。
	///
	/// 上流 EmueraConsole.verticalScrollBarUpdate は表示行が減ったとき
	/// Maximum を下げるだけで済ませ、Value の締め直しを WinForms に任せている。
	/// ここが欠けると Value &gt; Maximum のまま残り、画面全体が上へずれて下が空く。
	/// </summary>
	static int SelfTestScroll()
	{
		int ng = 0;

		void Check(string label, int actual, int expected)
		{
			bool ok = actual == expected;
			if (!ok)
				ng++;
			Console.WriteLine($"{(ok ? "OK  " : "NG  ")}{label}: {actual} (期待 {expected})");
		}

		Console.WriteLine("--- ScrollBar の範囲クランプ ---");

		// 表示行が減ったとき (CLEARLINE / 画面クリア) に最下行表示を保てること
		var bar = new System.Windows.Forms.VScrollBar { Minimum = 0, Maximum = 100, Value = 100 };
		bar.Maximum = 20;
		Check("Maximum を 100→20 に下げたときの Value", bar.Value, 20);

		// 全消し (CLEARLINE LINECOUNT) 相当
		bar.Maximum = 0;
		Check("Maximum を 0 にしたときの Value", bar.Value, 0);

		// 増える側は素通し (上流は Value += move で位置を保つ)
		bar.Maximum = 50;
		Check("Maximum を増やしても Value は動かない", bar.Value, 0);

		// 範囲外の代入
		bar.Value = 999;
		Check("Maximum を超える代入", bar.Value, 50);
		bar.Value = -5;
		Check("Minimum を下回る代入", bar.Value, 0);

		// Minimum 側
		bar.Minimum = 10;
		Check("Minimum を上げたときの Value", bar.Value, 10);

		// 上流 Designer と同じ設定 (Value == Maximum を「最下行」と判定するため)
		Check("LargeChange の既定", new System.Windows.Forms.VScrollBar().LargeChange, 1);

		// 変更通知が飛ぶこと (上流は行内入力欄の位置切り替えに使う)
		int fired = 0;
		var watched = new System.Windows.Forms.VScrollBar { Minimum = 0, Maximum = 10, Value = 10 };
		watched.ValueChanged += (_, _) => fired++;
		watched.Maximum = 3;      // Value が 10→3 に下がる
		watched.Value = 3;        // 変化なし
		Check("ValueChanged の発火回数", fired, 1);

		Console.WriteLine(ng == 0 ? "すべて OK" : $"NG {ng} 件");
		return ng == 0 ? 0 : 1;
	}

	/// <summary>
	/// パス正規化 (PortablePath) を Android 側の区切り '/' と Windows 側の '\' の両方で検証する。
	/// 実機でしか再現しない不具合なので、区切り文字を引数に取れるようにして PC 上で確かめる。
	/// ゲームフォルダを渡すと、実データに対する EXISTFILE / ENUMFILES 相当も確認する。
	/// </summary>
	static int SelfTestPath(string? gameDir)
	{
		int ng = 0;

		// (入力, Android での期待, Windows での期待)。null は「受け付けない」。
		(string input, string? onAndroid, string? onWindows)[] cases =
		[
			("resources/1001ペコリーヌ/顔_デフォルト.webp",
				"/data/game/resources/1001ペコリーヌ/顔_デフォルト.webp",
				@"D:\game\resources\1001ペコリーヌ\顔_デフォルト.webp"),
			(@"resources\1001ペコリーヌ\顔_デフォルト.webp",
				"/data/game/resources/1001ペコリーヌ/顔_デフォルト.webp",
				@"D:\game\resources\1001ペコリーヌ\顔_デフォルト.webp"),
			("resources/1001ペコリーヌ/",  "/data/game/resources/1001ペコリーヌ", @"D:\game\resources\1001ペコリーヌ"),
			("dat/人物DT_XML.txt",        "/data/game/dat/人物DT_XML.txt",       @"D:\game\dat\人物DT_XML.txt"),
			("a/./b",                     "/data/game/a/b",                      @"D:\game\a\b"),
			("a/../b",                    "/data/game/b",                        @"D:\game\b"),
			// ".." は上流同様に黙って落とすが、root より上へは出られない
			("../etc/passwd",             "/data/game/etc/passwd",               @"D:\game\etc\passwd"),
			// 上流の Replace("..\\","") は再走査しないので "..\etc" に化けて脱出できた
			("....//etc/passwd",          "/data/game/..../etc/passwd",          @"D:\game\....\etc\passwd"),
			("..",                        "/data/game/",                         @"D:\game\"),
			("",                          "/data/game/",                         @"D:\game\"),
			("/etc/passwd",               null,                                  null),
			(@"C:\Windows\win.ini",       null,                                  null),
			(@"\\server\share\x",         null,                                  null),
		];

		foreach (var (root, sep, label) in new[] { ("/data/game/", '/', "Android"), (@"D:\game\", '\\', "Windows") })
		{
			Console.WriteLine($"--- CombineUnderRoot  root={root} sep='{sep}'  ({label}) ---");
			foreach (var (input, onAndroid, onWindows) in cases)
			{
				string? expected = sep == '/' ? onAndroid : onWindows;
				string? got = PortablePath.CombineUnderRoot(root, input, sep);
				bool ok = got == expected;
				if (!ok)
					ng++;
				Console.WriteLine($"{(ok ? "OK  " : "NG  ")}\"{input}\" -> {got ?? "(null)"}"
					+ (ok ? "" : $"   期待: {expected ?? "(null)"}"));
			}
		}

		Console.WriteLine("--- Normalize ---");
		(string? input, char sep, string? expected)[] normCases =
		[
			("a/b/c",   '/',  "a/b/c"),
			(@"a\b\c",  '/',  "a/b/c"),
			("a/b/c",   '\\', @"a\b\c"),
			(@"a\b\c",  '\\', @"a\b\c"),
			("",        '/',  ""),
			(null,      '/',  null),
		];
		foreach (var (input, sep, expected) in normCases)
		{
			string? got = PortablePath.Normalize(input, sep);
			bool ok = got == expected;
			if (!ok)
				ng++;
			Console.WriteLine($"{(ok ? "OK  " : "NG  ")}\"{input ?? "(null)"}\" sep='{sep}' -> {got ?? "(null)"}"
				+ (ok ? "" : $"   期待: {expected ?? "(null)"}"));
		}

		if (gameDir != null)
			ng += CheckRealData(gameDir);

		Console.WriteLine(ng == 0 ? "すべて成功" : $"{ng} 件失敗");
		return ng == 0 ? 0 : 1;
	}

	/// <summary>
	/// PRINTC の桁揃えが成立する条件 (半角スペース = 半角文字、全角 = その 2 倍) を確かめる。
	///
	/// Windows にはシステムに BIZ UDGothic が入っているため素直に走らせると差が出ない。
	/// 「ゲームの font/ を使わない」「OS のフォントを名前で引かない」「名前で引けなかったときの
	/// 受け皿に何を据えるか」を切り替えて、実機で起きている状態を PC 上で再現し、
	/// 修正が効いていることを両側から見る。
	///
	/// ケースの組み立てはゲームによって変える。<b>font/ を同梱しないゲーム (eraTOWN など) は
	/// config のフォント名 (ＭＳ ゴシック) が Android に無いため必ず受け皿に落ちる</b>ので、
	/// 受け皿を据えたケースこそが実機の正常系になる。
	/// </summary>
	/// <param name="fontFallbackPath">
	/// 受け皿にするフォント。省略すると APK 同梱の BIZ UDGothic を探して使う。
	/// 利用者が共有 fonts/ に置いたフォントを再現したいときに渡す (期待値は決めず実測だけ出す)。
	/// </param>
	static int SelfTestFont(string gameDir, string? fontFallbackPath = null)
	{
		if (!Directory.Exists(gameDir))
		{
			Console.Error.WriteLine($"フォルダがありません: {gameDir}");
			return 1;
		}

		int ng = 0;

		bool hasGameFont = FindGameFontDir(gameDir) != null;
		string? fallback = fontFallbackPath ?? FindBundledFont();
		if (fallback == null)
			Console.WriteLine("※ APK 同梱フォントが見つからないため、受け皿ありのケースは飛ばします");
		Console.WriteLine($"ゲーム同梱 font/: {(hasGameFont ? "あり" : "なし")}");
		Console.WriteLine();

		// (ラベル, ゲームの font/ を使う, OS のフォントを使う, 受け皿のフォント, 等幅であるべきか)
		// 期待値 null は「環境によるので判定しない、実測だけ見せる」
		var cases = new List<(string label, bool gameFonts, bool systemFonts, string? fallback, bool? expectMonospaced)>();
		if (hasGameFont)
		{
			cases.Add(("ゲームの font/ あり (実機の正しい状態)", true, false, null, true));
			cases.Add(("ゲームの font/ なし (font/ を送り忘れた端末)", false, false, null, false));
		}
		else
		{
			cases.Add(("受け皿なし (同梱フォントも読めない最悪の端末)", false, false, null, false));
		}
		if (fallback != null)
			cases.Add(($"受け皿 = {Path.GetFileName(fallback)}"
					+ (hasGameFont ? " (font/ を送り忘れた端末の救済)" : " (実機の正しい状態)"),
				false, false, fallback, fontFallbackPath == null ? true : null));
		cases.Add(("Windows (OS のフォントも使える)", true, true, null, true));

		foreach (var (label, gameFonts, systemFonts, caseFallback, expectMonospaced) in cases)
		{
			Console.WriteLine($"--- {label} ---");
			var d = EmueraEngine.InspectFonts(gameDir, gameFonts, systemFonts, caseFallback);
			Console.WriteLine($"指定: {d.RequestedName} / 実際: {d.ActualName} / {d.FontSize}px");
			Console.WriteLine($"半角スペース×{FontSamples}={d.SpaceWidth}px  " +
							  $"半角M×{FontSamples}={d.LatinWidth}px  " +
							  $"全角×{FontSamples / 2}={d.FullWidth}px  " +
							  $"太字M×{FontSamples}={d.BoldLatinWidth}px");

			if (expectMonospaced.HasValue)
				ng += Check($"等幅である", d.IsMonospaced == expectMonospaced.Value,
					$"IsMonospaced={d.IsMonospaced} (期待 {expectMonospaced.Value})");
			else
				Console.WriteLine($"    等幅: {d.IsMonospaced}");

			if (d.IsMonospaced)
			{
				// 太字フェイスを持たないフォント (MS Gothic など) は、移植側の Embolden ではなく
				// OS が太らせたフェイスを返してくることがあり、そちらは送り幅が動く (本家 Windows と同じ挙動)。
				// 端末にそれらのフォントは無いので、OS のフォントを使うケースでは実測を見せるだけにする
				if (systemFonts)
					Console.WriteLine($"    太字の送り幅: 並字 {d.LatinWidth}px / 太字 {d.BoldLatinWidth}px");
				else
					ng += Check("太字でも送り幅が変わらない", d.BoldKeepsAdvance,
						$"並字 {d.LatinWidth}px / 太字 {d.BoldLatinWidth}px");

				// 等幅なら PRINTC はどのラベルも同じ枠幅に収まる。
				// 崩れているときはラベル素の幅がそのまま出て、隣とくっつく
				foreach (var (sample, padded, slot) in d.PrintCSamples)
					ng += Check($"PRINTC \"{sample}\" が {d.PrintCLength} 桁の枠に収まる",
						Math.Abs(padded - slot) <= 2, $"詰めた結果 {padded}px / 枠 {slot}px");
			}
			else
			{
				// 崩れている側では、詰めきれずに枠より広くなるラベルがあることを見せる
				foreach (var (sample, padded, slot) in d.PrintCSamples)
					Console.WriteLine($"    PRINTC \"{sample}\" -> {padded}px (枠 {slot}px)"
						+ (padded > slot + 2 ? "  ← はみ出して隣とくっつく" : ""));
			}
			Console.WriteLine();
		}

		Console.WriteLine(ng == 0 ? "すべて成功" : $"{ng} 件失敗");
		return ng == 0 ? 0 : 1;
	}

	/// <summary>
	/// ゲームが同梱している font/ フォルダ。Android は大小を区別するので、上流の
	/// <c>Program.ResolveDir</c> と同じく実在するほうの表記を採る。
	/// </summary>
	static string? FindGameFontDir(string gameDir)
	{
		foreach (string dir in Directory.EnumerateDirectories(gameDir))
			if (Path.GetFileName(dir).Equals("font", StringComparison.OrdinalIgnoreCase))
				return dir;
		return null;
	}

	/// <summary>
	/// APK に同梱している等幅フォントの実体。実機では <c>MainActivity.SetupFonts</c> が
	/// これを受け皿に据えるので、PC で実機の状態を再現するのに要る。
	/// ハーネスは bin/ の下から走るため、リポジトリのルートまで遡って探す。
	/// </summary>
	static string? FindBundledFont()
	{
		string relative = Path.Combine("src", "andEmuera.Android", "Assets", "fonts", "BIZUDGothic-Regular.ttf");
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
		{
			string path = Path.Combine(dir.FullName, relative);
			if (File.Exists(path))
				return path;
		}
		return null;
	}

	/// <summary>
	/// ゲームの ERB / CSV に出てくる文字のうち、本文フォントが持っていないものを洗い出す。
	///
	/// Skia は Windows の GDI+ と違って font linking をしないので、ここに挙がる文字は
	/// 代替フォントへ回さない限り端末で豆腐 (□) になる。
	/// あわせて「代替へ回しても桁が動かない」ことを確かめる — 送り幅が主フォントの
	/// 半角/全角セルちょうどで、前後の文字の位置を 1px も動かさないこと。
	/// </summary>
	static int SelfTestGlyph(string gameDir, string? shotPath)
	{
		if (!Directory.Exists(gameDir))
		{
			Console.Error.WriteLine($"フォルダがありません: {gameDir}");
			return 1;
		}

		var font = EmueraEngine.ResolveDefaultFont(gameDir);
		if (font == null)
		{
			Console.Error.WriteLine("本文フォントを解決できませんでした");
			return 1;
		}

		float half = Advance(" ", font);
		float full = Advance("　", font);
		Console.WriteLine($"本文フォント: 指定 {font.Name} / 実際 {font.FontFamily.Name} / {font.Size}px");
		Console.WriteLine($"半角セル {half}px / 全角セル {full}px");
		Console.WriteLine();

		var counts = new Dictionary<int, int>();
		int files = 0, total = 0;
		foreach (string path in EnumerateScripts(gameDir))
		{
			files++;
			string text = ReadScript(path);
			for (int i = 0; i < text.Length; i++)
			{
				char c = text[i];
				if (c < 0x80 || c == '﻿')
					continue;
				int codePoint = c;
				if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
					codePoint = char.ConvertToUtf32(c, text[++i]);
				else if (char.IsSurrogate(c))
					continue;                      // 対を成さないサロゲート。数えても仕方がない
				if (System.Drawing.GlyphFallback.Covers(font, codePoint))
					continue;
				counts[codePoint] = counts.GetValueOrDefault(codePoint) + 1;
				total++;
			}
		}

		var missing = counts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
		Console.WriteLine($"走査 {files} ファイル — フォントに無い文字 {missing.Count} 種 / 延べ {total} 文字");

		int unresolved = 0;
		foreach (int codePoint in missing.Take(GlyphReportLimit))
		{
			var face = System.Drawing.GlyphFallback.Substitute(codePoint);
			Console.WriteLine($"  U+{codePoint:X4} {char.ConvertFromUtf32(codePoint)} {counts[codePoint],6} 回  → " +
							  (face?.FamilyName ?? "代替なし"));
		}
		foreach (int codePoint in missing)
			if (System.Drawing.GlyphFallback.Substitute(codePoint) == null)
				unresolved++;
		Console.WriteLine();

		int ng = 0;
		ng += Check("すべての欠け文字に代替フェイスが見つかる", unresolved == 0,
			$"代替なし {unresolved} 種 / 全 {missing.Count} 種");

		foreach (int codePoint in missing.Take(GlyphProbeLimit))
		{
			string s = char.ConvertFromUtf32(codePoint);
			string label = $"U+{codePoint:X4} {s}";

			// 送り幅は半角セルか全角セルちょうど。ここがずれると PRINTC の桁が動く
			float w = Advance(s, font);
			ng += Check($"{label} の送り幅がセルちょうど",
				Near(w, half) || Near(w, full), $"{w}px (半角 {half} / 全角 {full})");

			// 前後の文字を押しのけない = 幅が素直に加算される
			float mixed = Advance("あ" + s + "い", font);
			float apart = Advance("あい", font) + w;
			ng += Check($"{label} を挟んでも前後がずれない", Near(mixed, apart), $"{mixed}px / {apart}px");
		}

		// 欠けの無い文字列は従来どおりの経路 (等幅のまま) で測られる
		ng += Check("欠けの無い文字列は等幅のまま", Near(Advance("あいうえお", font), full * 5),
			$"{Advance("あいうえお", font)}px / 全角 5 個 {full * 5}px");

		if (shotPath != null)
		{
			RenderGlyphSheet(font, missing.Take(GlyphReportLimit).ToList(), full, shotPath);
			Console.WriteLine($"見本を書き出しました: {shotPath}");
		}

		Console.WriteLine();
		Console.WriteLine(ng == 0 ? "すべて成功" : $"{ng} 件失敗");
		return ng == 0 ? 0 : 1;
	}

	/// <summary>一覧に出す件数と、不変条件を確かめる件数。</summary>
	const int GlyphReportLimit = 20;
	const int GlyphProbeLimit = 8;

	/// <summary>
	/// 欠け文字を本文フォントと同じ経路で実際に描いた見本を書き出す。
	/// 全角セルごとに縦の目盛りを引いてあるので、
	/// 「字が出ているか」と「桁が動いていないか」を 1 枚で見られる。
	/// </summary>
	static void RenderGlyphSheet(System.Drawing.Font font, List<int> codePoints, float cell, string path)
	{
		const int Margin = 12;
		int lineHeight = font.Height + 4;
		int textLeft = Margin + (int)(cell * 6);            // 見出しのぶんだけ空ける
		int width = textLeft + (int)(cell * 14) + Margin;
		int height = Margin * 2 + lineHeight * (codePoints.Count + 1);

		using var bmp = new System.Drawing.Bitmap(width, height);
		using var g = System.Drawing.Graphics.FromImage(bmp);
		g.Clear(System.Drawing.Color.Black);

		// 全角セルの目盛り。代替グリフがここをまたいだら桁が動いている
		using (var guide = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 40, 40, 60)))
			for (float x = textLeft; x < width - Margin; x += cell)
				g.DrawLine(guide, x, Margin, x, height - Margin);

		var fore = System.Drawing.Color.FromArgb(255, 220, 220, 220);
		var accent = System.Drawing.Color.FromArgb(255, 120, 200, 255);
		int y = Margin;

		System.Windows.Forms.TextRenderer.DrawText(g, "あいうえおかきくけこ", font,
			new System.Drawing.Point(textLeft, y), accent);
		System.Windows.Forms.TextRenderer.DrawText(g, "基準", font, new System.Drawing.Point(Margin, y), accent);
		y += lineHeight;

		foreach (int codePoint in codePoints)
		{
			string s = char.ConvertFromUtf32(codePoint);
			System.Windows.Forms.TextRenderer.DrawText(g, $"{codePoint:X4}", font,
				new System.Drawing.Point(Margin, y), accent);
			// 前後を既知の全角文字で挟む。ずれれば目盛りに対して一目で分かる
			System.Windows.Forms.TextRenderer.DrawText(g, $"あ{s}い{s}{s}うえ", font,
				new System.Drawing.Point(textLeft, y), fore);
			y += lineHeight;
		}

		bmp.Save(path);
	}

	static float Advance(string text, System.Drawing.Font font)
		=> System.Drawing.GlyphFallback.Measure(font, text);

	static bool Near(float a, float b) => Math.Abs(a - b) < 0.01f;

	static IEnumerable<string> EnumerateScripts(string gameDir)
	{
		var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
		foreach (string path in Directory.EnumerateFiles(gameDir, "*", options))
		{
			string ext = Path.GetExtension(path);
			if (ext.Equals(".erb", StringComparison.OrdinalIgnoreCase) ||
				ext.Equals(".erh", StringComparison.OrdinalIgnoreCase) ||
				ext.Equals(".csv", StringComparison.OrdinalIgnoreCase))
				yield return path;
		}
	}

	/// <summary>era のスクリプトは UTF-8 と Shift-JIS が混在している。厳密 UTF-8 で読めなければ CP932。</summary>
	static string ReadScript(string path)
	{
		byte[] raw = File.ReadAllBytes(path);
		try
		{
			return new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(raw);
		}
		catch (ArgumentException)
		{
			return System.Text.Encoding.GetEncoding("Shift-JIS").GetString(raw);
		}
	}

	const int FontSamples = EmueraEngine.FontDiagnostics.SampleCount;

	static int Check(string what, bool ok, string detail)
	{
		Console.WriteLine($"{(ok ? "OK  " : "NG  ")}{what}   ({detail})");
		return ok ? 0 : 1;
	}

	/// <summary>
	/// 実データに対して EXISTFILE / ENUMFILES 相当を叩く。ゲームフォルダは読むだけ
	/// (Program.Initialize はパスを組み立てるだけで、ロードも emuera.log の出力もしない)。
	/// </summary>
	static int CheckRealData(string gameDir)
	{
		Console.WriteLine($"--- 実データ ({gameDir}) ---");
		if (!Directory.Exists(gameDir))
		{
			Console.WriteLine($"NG  フォルダがありません");
			return 1;
		}

		MinorShift.Emuera.Program.Initialize(gameDir);
		string contentDir = MinorShift.Emuera.Program.ContentDir;
		Console.WriteLine($"ContentDir: {contentDir}  存在={Directory.Exists(contentDir)}");
		if (!Directory.Exists(contentDir))
		{
			Console.WriteLine("NG  resources がありません");
			return 1;
		}

		// 画像を持つキャラフォルダを 1 つ拾い、ERB と同じ形の相対パスで実在確認する
		string? sample = Directory.EnumerateDirectories(contentDir)
			.SelectMany(d => Directory.EnumerateFiles(d, "顔_*.webp"))
			.FirstOrDefault();
		if (sample == null)
		{
			Console.WriteLine("SKIP 顔_*.webp を持つキャラフォルダがありません (resources 未配置)");
			return 0;
		}

		// ERB は "resources/<フォルダ>/<ファイル>" を '/' 区切りで渡してくる
		string relative = Path.GetRelativePath(MinorShift.Emuera.Program.ExeDir, sample).Replace('\\', '/');
		int ng = 0;

		// EXISTFILE 相当 (GetValidPath の中身)
		string? resolved = PortablePath.CombineUnderRoot(MinorShift.Emuera.Program.ExeDir, relative);
		bool exists = resolved != null && File.Exists(resolved);
		if (!exists)
			ng++;
		Console.WriteLine($"{(exists ? "OK  " : "NG  ")}EXISTFILE(\"{relative}\") -> {resolved}");

		// GCREATEFROMFILE 相当 (ContentDir + Normalize)
		string underContent = relative.StartsWith("resources/") ? relative["resources/".Length..] : relative;
		string imgPath = contentDir + PortablePath.Normalize(underContent);
		bool imgOk = File.Exists(imgPath);
		if (!imgOk)
			ng++;
		Console.WriteLine($"{(imgOk ? "OK  " : "NG  ")}GCREATEFROMFILE(\"{underContent}\") -> {imgPath}");

		// ENUMFILES 相当
		string charDir = Path.GetDirectoryName(relative)!.Replace('\\', '/');
		string? enumDir = PortablePath.CombineUnderRoot(MinorShift.Emuera.Program.ExeDir, charDir);
		// Config.CaseInsensitiveTopDirectory と同じ設定 (Config は internal なのでここで組む)
		var options = new EnumerationOptions
		{
			MatchCasing = MatchCasing.CaseInsensitive,
			MatchType = MatchType.Win32,
			AttributesToSkip = 0,
			RecurseSubdirectories = false,
			IgnoreInaccessible = true,
		};
		int count = enumDir != null && Directory.Exists(enumDir)
			? Directory.EnumerateFiles(enumDir, "顔_*.*", options).Count()
			: -1;
		if (count <= 0)
			ng++;
		Console.WriteLine($"{(count > 0 ? "OK  " : "NG  ")}ENUMFILES(\"{charDir}\", \"顔_*.*\") -> {count} 件");

		return ng;
	}

	static int CountFiles(string dir, string pattern)
		=> Directory.Exists(dir) ? Directory.GetFiles(dir, pattern, SearchOption.AllDirectories).Length : 0;

	/// <summary>emuera.log に出た警告・エラーを集計して表示する。</summary>
	static void ReportLog(string gameDir)
	{
		string logPath = Path.Combine(gameDir, "emuera.log");
		if (!File.Exists(logPath))
		{
			Console.WriteLine("emuera.log は出力されていません (警告なし)");
			return;
		}

		var lines = File.ReadAllLines(logPath);
		Console.WriteLine($"emuera.log: {lines.Length} 行");
		foreach (var line in lines.Take(40))
			Console.WriteLine("  " + line);
		if (lines.Length > 40)
			Console.WriteLine($"  ... 他 {lines.Length - 40} 行");
	}
}
