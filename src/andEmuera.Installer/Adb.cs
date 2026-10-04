using System.Diagnostics;
using System.Text;

namespace AndEmuera.Installer;

/// <summary><c>adb devices -l</c> の 1 行。</summary>
sealed record AdbDevice(string Serial, string State, string Model)
{
	public bool Ready => State == "device";

	public override string ToString() => State switch
	{
		"device" => $"{Model} ({Serial})",
		"unauthorized" => $"{Serial} — 端末の画面で USB デバッグを許可してください",
		"offline" => $"{Serial} — 応答がありません (つなぎ直してください)",
		_ => $"{Serial} — {State}",
	};
}

sealed class AdbException(string message) : Exception(message);

/// <summary>
/// adb.exe の呼び出し。出力は UTF-8 として読む (日本語のパスがそのまま返る)。
/// </summary>
sealed class Adb(string path)
{
	public string ExePath { get; } = path;

	/// <summary>以降の呼び出しで <c>-s</c> に渡す端末。途中で 2 台目が刺さっても取り違えないよう常に付ける。</summary>
	public string? Serial { get; set; }

	static readonly UTF8Encoding Utf8NoBom = new(false);

	/// <summary>
	/// adb.exe を探す。配布物では exe の隣の adb/ に同梱している。
	/// 開発機では PATH と Android SDK も見る。
	/// </summary>
	public static string? Find()
	{
		string baseDir = AppContext.BaseDirectory;
		var candidates = new List<string>
		{
			Path.Combine(baseDir, "adb", "adb.exe"),
			Path.Combine(baseDir, "adb.exe"),
		};
		foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
			candidates.Add(Path.Combine(dir.Trim('"'), "adb.exe"));
		foreach (string? sdk in new[]
		{
			Environment.GetEnvironmentVariable("ANDROID_HOME"),
			Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Android", "android-sdk"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Android", "android-sdk"),
		})
		{
			if (!string.IsNullOrEmpty(sdk)) candidates.Add(Path.Combine(sdk, "platform-tools", "adb.exe"));
		}
		return candidates.FirstOrDefault(File.Exists);
	}

	public readonly record struct Result(int ExitCode, string Output);

	/// <summary>adb を走らせる。中止されたら adb ごと止める。</summary>
	public async Task<Result> RunAsync(IEnumerable<string> args, CancellationToken ct = default, bool withSerial = true)
	{
		var psi = new ProcessStartInfo(ExePath)
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			StandardOutputEncoding = Utf8NoBom,
			StandardErrorEncoding = Utf8NoBom,
		};
		if (withSerial && Serial != null)
		{
			psi.ArgumentList.Add("-s");
			psi.ArgumentList.Add(Serial);
		}
		foreach (string a in args) psi.ArgumentList.Add(a);

		using var p = new Process { StartInfo = psi };
		var sb = new StringBuilder();
		void Line(string? l)
		{
			if (l == null) return;
			lock (sb) sb.Append(l).Append('\n');
		}
		p.OutputDataReceived += (_, e) => Line(e.Data);
		p.ErrorDataReceived += (_, e) => Line(e.Data);
		p.Start();
		p.BeginOutputReadLine();
		p.BeginErrorReadLine();
		try
		{
			await p.WaitForExitAsync(ct);
		}
		catch (OperationCanceledException)
		{
			try { p.Kill(true); } catch { }
			throw;
		}
		// 非同期読み取りの残りを流し切る
		p.WaitForExit();
		lock (sb) return new Result(p.ExitCode, sb.ToString());
	}

	/// <summary>失敗 (終了コード ≠ 0) なら例外にする。</summary>
	public async Task<string> CheckedAsync(CancellationToken ct, params string[] args)
	{
		var r = await RunAsync(args, ct);
		if (r.ExitCode != 0)
		{
			// 一覧を取るコマンドだと出力が数万行あるので、末尾だけ見せる
			var tail = r.Output.TrimEnd().Split('\n').TakeLast(15);
			throw new AdbException($"adb {string.Join(' ', args.Take(2))} が失敗しました (終了コード {r.ExitCode})\n{string.Join('\n', tail)}");
		}
		return r.Output;
	}

	public async Task<List<AdbDevice>> DevicesAsync(CancellationToken ct = default)
	{
		var r = await RunAsync(["devices", "-l"], ct, withSerial: false);
		var list = new List<AdbDevice>();
		foreach (string raw in r.Output.Split('\n'))
		{
			string line = raw.Trim();
			if (line.Length == 0 || line.StartsWith("List of devices") || line.StartsWith('*')) continue;
			var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length < 2) continue;
			string model = parts.FirstOrDefault(x => x.StartsWith("model:"))?["model:".Length..].Replace('_', ' ') ?? parts[0];
			list.Add(new AdbDevice(parts[0], parts[1], model));
		}
		return list;
	}

	public Task<string> ShellAsync(string command, CancellationToken ct = default) => CheckedAsync(ct, "shell", command);

	/// <summary>
	/// 端末でシェルスクリプトを走らせる。日本語のパスをコマンドラインに載せると
	/// adb.exe → 端末の sh のどこかで引用や文字コードが崩れうるので、UTF-8 のファイルにして送ってから sh で読ませる。
	/// </summary>
	public async Task<string> ScriptAsync(string script, CancellationToken ct = default)
	{
		string local = Path.Combine(Path.GetTempPath(), $"andemuera-{Guid.NewGuid():N}.sh");
		string remote = $"/data/local/tmp/andemuera-{Guid.NewGuid():N}.sh";
		await File.WriteAllTextAsync(local, script.Replace("\r\n", "\n"), Utf8NoBom, ct);
		try
		{
			await CheckedAsync(ct, "push", local, remote);
		}
		finally
		{
			File.Delete(local);
		}
		return await CheckedAsync(ct, "shell", $"sh {remote}; r=$?; rm -f {remote}; exit $r");
	}

	/// <summary>sh の単一引用符で包む。</summary>
	public static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
