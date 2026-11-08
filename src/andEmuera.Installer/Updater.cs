using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace AndEmuera.Installer;

/// <summary>GitHub Releases の最新版。</summary>
sealed record ReleaseInfo(string Version, string Name, string PageUrl, string Notes, string ZipUrl, long ZipSize, string? Sha256);

/// <summary>
/// かんたん転送の自己更新。GitHub Releases の最新版を調べ、配布 zip (andEmuera-&lt;版&gt;.zip) を落として
/// exe の隣のファイル (exe・APK・adb・README など) を入れ替える。APK も入れ替わるので、
/// 開き直したあと「andEmuera を更新する」で端末のアプリも新しくできる。
///
/// 動いている exe は消せないが改名はできるので、古いファイルは *.old-aeu に改名して退け、
/// 次に起動したときに消す。途中で失敗したら退けたものを戻す。
/// </summary>
static class Updater
{
	const string Repo = "BluishCat/andEmuera";
	const string ExeName = "andEmueraInstaller.exe";
	const string OldSuffix = ".old-aeu";
	const string NewDirName = ".aeu-new";
	const string PartPrefix = ".aeu-";

	static readonly HttpClient Http = CreateClient();

	static HttpClient CreateClient()
	{
		var h = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
		// GitHub API は User-Agent の無い要求を断る
		h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("andEmueraInstaller", OwnVersion ?? "dev"));
		return h;
	}

	static string BaseDir => AppContext.BaseDirectory;

	/// <summary>
	/// この exe の版 (pack.ps1 が InformationalVersion に入れる)。開発中のビルドは null。
	/// </summary>
	public static string? OwnVersion
	{
		get
		{
			string? v = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
			if (v == null) return null;
			int plus = v.IndexOf('+');
			if (plus >= 0) v = v[..plus];
			return v is "" or "dev" or "1.0.0" ? null : v;
		}
	}

	/// <summary>最新のリリースを調べる。配布 zip が付いていなければ null。</summary>
	public static async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		cts.CancelAfter(TimeSpan.FromSeconds(15));
		using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
		req.Headers.Accept.ParseAdd("application/vnd.github+json");
		using var res = await Http.SendAsync(req, cts.Token);
		res.EnsureSuccessStatusCode();
		await using var body = await res.Content.ReadAsStreamAsync(cts.Token);
		using var doc = await JsonDocument.ParseAsync(body, cancellationToken: cts.Token);
		var r = doc.RootElement;

		string tag = r.GetProperty("tag_name").GetString() ?? "";
		string version = tag.TrimStart('v', 'V');
		string zipName = $"andEmuera-{version}.zip";
		foreach (var a in r.GetProperty("assets").EnumerateArray())
		{
			if (a.GetProperty("name").GetString() != zipName) continue;
			// digest は "sha256:<16 進>"。古いリリースには無いこともある
			string? sha = a.TryGetProperty("digest", out var d) && d.GetString() is { } ds && ds.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
				? ds["sha256:".Length..] : null;
			return new ReleaseInfo(
				version,
				r.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } ns ? ns : tag,
				r.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases",
				r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
				a.GetProperty("browser_download_url").GetString()!,
				a.GetProperty("size").GetInt64(),
				sha);
		}
		return null;
	}

	/// <summary>
	/// 版を比べる。この配布物は 0.8 → 0.81 → 0.83 → 0.9 → 0.91 のように小数として増やしているので、
	/// 両方が小数として読めれば小数で比べる (System.Version だと 0.83 が 0.9 より新しくなってしまう)。
	/// </summary>
	public static int Compare(string a, string b)
	{
		const NumberStyles style = NumberStyles.AllowDecimalPoint;
		if (decimal.TryParse(a, style, CultureInfo.InvariantCulture, out var da) &&
			decimal.TryParse(b, style, CultureInfo.InvariantCulture, out var db))
			return da.CompareTo(db);
		if (Version.TryParse(a, out var va) && Version.TryParse(b, out var vb))
			return va.CompareTo(vb);
		return string.CompareOrdinal(a, b);
	}

	/// <summary>exe の隣に書き込めるか (Program Files などに置かれていると書けない)。</summary>
	public static bool CanWriteBaseDir()
	{
		try
		{
			string probe = Path.Combine(BaseDir, $"{PartPrefix}probe-{Environment.ProcessId}");
			File.WriteAllText(probe, "");
			File.Delete(probe);
			return true;
		}
		catch (Exception) { return false; }
	}

	public static void OpenReleasePage(string url) =>
		Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

	/// <summary>配布 zip を exe の隣に落とし、大きさと SHA-256 を確かめる。落としたファイルのパスを返す。</summary>
	public static async Task<string> DownloadAsync(ReleaseInfo rel, IProgress<(long Done, long Total)> progress, CancellationToken ct)
	{
		string path = Path.Combine(BaseDir, $"{PartPrefix}{rel.Version}.zip.part");
		try
		{
			using var res = await Http.GetAsync(rel.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
			res.EnsureSuccessStatusCode();
			long total = res.Content.Headers.ContentLength ?? rel.ZipSize;
			string hash;
			long done = 0;
			await using (var src = await res.Content.ReadAsStreamAsync(ct))
			await using (var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
			{
				using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
				var buf = new byte[1 << 16];
				int n;
				var last = Stopwatch.StartNew();
				while ((n = await src.ReadAsync(buf, ct)) > 0)
				{
					sha.AppendData(buf, 0, n);
					await dst.WriteAsync(buf.AsMemory(0, n), ct);
					done += n;
					if (last.ElapsedMilliseconds >= 100) { progress.Report((done, total)); last.Restart(); }
				}
				progress.Report((done, total));
				hash = Convert.ToHexStringLower(sha.GetHashAndReset());
			}
			if (rel.ZipSize > 0 && done != rel.ZipSize)
				throw new InvalidDataException($"ダウンロードが途中で切れました ({done:N0} / {rel.ZipSize:N0} バイト)。");
			if (rel.Sha256 != null && !hash.Equals(rel.Sha256, StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("ダウンロードしたファイルが壊れています (SHA-256 が一致しません)。");
			return path;
		}
		catch
		{
			TryDelete(path);
			throw;
		}
	}

	/// <summary>
	/// 落とした zip を展開し、exe の隣のファイルを入れ替える。入れ替えたあとの exe のパスを返す。
	/// zip に入っているファイルだけを入れ替え、利用者が置いたほかのファイルには触らない。
	/// 古い版の APK だけは退ける (exe の隣の APK を「同梱の andEmuera」として使うので、取り違えないように)。
	/// </summary>
	public static string Apply(string zipPath)
	{
		string newDir = Path.Combine(BaseDir, NewDirName);
		if (Directory.Exists(newDir)) Directory.Delete(newDir, true);
		try
		{
			ZipFile.ExtractToDirectory(zipPath, newDir);
			// zip の中は andEmuera-<版>/ の 1 段下にある
			string src = newDir;
			if (Directory.GetFiles(newDir).Length == 0 && Directory.GetDirectories(newDir) is [string only]) src = only;
			if (!File.Exists(Path.Combine(src, ExeName)))
				throw new InvalidDataException($"更新の zip に {ExeName} が入っていません。");

			// 利用者が exe の名前を変えていても、その名前のまま入れ替える
			string myExe = Path.GetFileName(Environment.ProcessPath ?? ExeName);
			string DestRel(string rel) => rel.Equals(ExeName, StringComparison.OrdinalIgnoreCase) ? myExe : rel;

			var files = Directory.GetFiles(src, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(src, f)).ToList();
			var incoming = files.Select(DestRel).ToHashSet(StringComparer.OrdinalIgnoreCase);
			var retire = Directory.GetFiles(BaseDir, "*.apk").Select(f => Path.GetFileName(f)).Where(n => !incoming.Contains(n)).ToList();

			// (置き場所, 退けた古いファイル, 新しいファイルを置いたか)。失敗したら逆順に戻す
			var steps = new List<(string Dest, string? Old, bool Placed)>();
			try
			{
				foreach (string name in retire)
				{
					string dest = Path.Combine(BaseDir, name);
					File.Move(dest, dest + OldSuffix, true);
					steps.Add((dest, dest + OldSuffix, false));
				}
				foreach (string rel in files)
				{
					string dest = Path.Combine(BaseDir, DestRel(rel));
					Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
					string? old = null;
					if (File.Exists(dest))
					{
						old = dest + OldSuffix;
						File.Move(dest, old, true);
					}
					steps.Add((dest, old, false));
					File.Move(Path.Combine(src, rel), dest);
					steps[^1] = (dest, old, true);
				}
			}
			catch
			{
				for (int i = steps.Count - 1; i >= 0; i--)
				{
					var (dest, old, placed) = steps[i];
					try
					{
						if (placed) File.Delete(dest);
						if (old != null) File.Move(old, dest, true);
					}
					catch { }
				}
				throw;
			}
			return Path.Combine(BaseDir, myExe);
		}
		finally
		{
			try { Directory.Delete(newDir, true); } catch { }
			TryDelete(zipPath);
		}
	}

	/// <summary>
	/// 前回の更新で退けた古いファイルと、途中で止まった更新の残りを消す。
	/// 古い exe はまだ終わりきっていないことがあるので、少し待ちながら何度か試す。
	/// </summary>
	public static Task CleanupAsync() => Task.Run(async () =>
	{
		for (int attempt = 0; attempt < 10; attempt++)
		{
			bool left = false;
			try
			{
				var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true, AttributesToSkip = 0 };
				foreach (string f in Directory.EnumerateFiles(BaseDir, "*" + OldSuffix, opts))
					if (!TryDelete(f)) left = true;
				foreach (string f in Directory.EnumerateFiles(BaseDir, PartPrefix + "*.zip.part"))
					if (!TryDelete(f)) left = true;
				string newDir = Path.Combine(BaseDir, NewDirName);
				if (Directory.Exists(newDir))
				{
					try { Directory.Delete(newDir, true); } catch { left = true; }
				}
			}
			catch { }
			if (!left) return;
			await Task.Delay(1000);
		}
	});

	public static void Restart(string exe) =>
		Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });

	static bool TryDelete(string path)
	{
		try
		{
			if (!File.Exists(path)) return true;
			File.SetAttributes(path, FileAttributes.Normal);
			File.Delete(path);
			return true;
		}
		catch { return false; }
	}
}
