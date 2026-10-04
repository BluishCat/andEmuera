using System.Text.RegularExpressions;

namespace AndEmuera.Installer;

/// <summary>端末と、そこに入っている andEmuera・ゲームの様子。</summary>
sealed record DeviceInfo(
	string Model, string AndroidVersion, long FreeBytes,
	string? AppVersionName, string? AppVersionCode, bool AppDebuggable,
	List<RemoteGame> Games)
{
	public bool AppInstalled => AppVersionCode != null;
}

/// <summary>端末の games/ にあるフォルダ。csv と erb を両方持つものがアプリから遊べる。</summary>
sealed record RemoteGame(string Name, bool Playable);

static partial class Device
{
	public const string AppId = "rip.eragames.andemuera";
	public const string FilesDir = "/sdcard/Android/data/" + AppId + "/files";
	public const string GamesDir = FilesDir + "/games";

	/// <summary>転送の途中で使う一時フォルダの名前。games/ の一覧からは隠す。</summary>
	public const string StagePrefix = "zz_andemuera_";

	public static async Task<DeviceInfo> QueryAsync(Adb adb, CancellationToken ct = default)
	{
		string manufacturer = (await adb.ShellAsync("getprop ro.product.manufacturer", ct)).Trim();
		string model = (await adb.ShellAsync("getprop ro.product.model", ct)).Trim();
		string release = (await adb.ShellAsync("getprop ro.build.version.release", ct)).Trim();

		// 空き容量。df -k の最終行の 4 列目 (Available, KB)
		long free = -1;
		string df = await adb.ShellAsync("df -k /sdcard", ct);
		var last = df.Trim().Split('\n').LastOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (last is { Length: >= 4 } && long.TryParse(last[3], out long kb)) free = kb * 1024;

		string dump = await adb.ShellAsync($"dumpsys package {AppId}", ct);
		string? name = null, code = null;
		bool debuggable = false;
		var mc = VersionCodeRegex().Match(dump);
		if (mc.Success)
		{
			code = mc.Groups[1].Value;
			name = VersionNameRegex().Match(dump) is { Success: true } mn ? mn.Groups[1].Value : "?";
			debuggable = DebuggableRegex().IsMatch(dump);
		}

		var games = new List<RemoteGame>();
		// フォルダ名は大文字小文字を問わず csv / erb を探す (アプリと同じ)
		string list = await adb.ScriptAsync($$"""
			cd {{Adb.Q(GamesDir)}} 2>/dev/null || exit 0
			for d in */; do
			  d=${d%/}
			  [ -d "$d" ] || continue
			  c=0; e=0
			  for x in "$d"/*; do
			    case "${x##*/}" in [Cc][Ss][Vv]) c=1;; [Ee][Rr][Bb]) e=1;; esac
			  done
			  echo "$c$e $d"
			done
			""", ct);
		foreach (string line in list.Split('\n'))
		{
			if (line.Length < 4 || line[2] != ' ') continue;
			string dir = line[3..];
			if (dir.StartsWith(StagePrefix)) continue;
			games.Add(new RemoteGame(dir, line[0] == '1' && line[1] == '1'));
		}
		games.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

		string displayModel = model.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase) || manufacturer.Length == 0
			? model : $"{manufacturer} {model}";
		return new DeviceInfo(displayModel, release, free, name, code, debuggable, games);
	}

	/// <summary>
	/// この APK を入れてよいか。だめなら利用者向けの理由を返す。
	/// adb に任せて失敗させると、adb が自分でアンインストールして入れ直すことがあり
	/// (古い Debug APK で実際に起きた)、端末のゲームとセーブが消える。危ないものは adb を呼ぶ前に止める。
	/// </summary>
	public static string? CheckInstallable(ApkInfo apk, DeviceInfo? device)
	{
		if (apk.Package != AppId)
			return $"これは andEmuera の APK ではありません ({apk.Package})。";
		if (apk.Debuggable || !apk.HasCode)
			return "これは開発用 (Debug) の APK です。配布用の APK を選んでください。";
		if (device is not { AppInstalled: true }) return null;

		long installed = long.TryParse(device.AppVersionCode, out long c) ? c : 0;
		if (apk.VersionCode < installed)
			return $"端末に入っている版 ({device.AppVersionName}) のほうが新しいので、この版 ({apk.VersionName}) は入れられません。";
		if (device.AppDebuggable)
			// 端末のものは開発用の鍵、この APK は配布用の鍵で署名されている。上書きはできず、
			// 入れ替えるにはアンインストール (= ゲームとセーブが消える) が要る
			return "端末に入っている andEmuera は開発用の版です。配布用の版で上書きすることはできません。\n\n" +
				"入れ替えるには、端末で andEmuera をアンインストールする必要があります (端末に送ったゲームとセーブも消えます)。\n" +
				"続けるなら、先に「セーブを PC に保存」で控えてから、端末の設定でアンインストールしてください。";
		return null;
	}

	/// <summary>アプリを入れる (上書き)。失敗したら利用者向けの説明を付けて例外にする。</summary>
	public static async Task InstallAsync(Adb adb, string apkPath, ApkInfo apk, DeviceInfo? device, CancellationToken ct = default)
	{
		if (CheckInstallable(apk, device) is { } problem) throw new AdbException(problem);

		// -r は上書き。-d (ダウングレード) は決して付けない。
		// --no-incremental: incremental install は失敗時の振る舞いが読めない (アンインストールされた例がある) ので使わない
		var r = await adb.RunAsync(["install", "-r", "--no-incremental", apkPath], ct);
		if (r.Output.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE"))
			throw new AdbException(
				"端末に入っている andEmuera と署名が違うため、上書きできませんでした。\n\n" +
				"入れ替えるには端末で andEmuera をアンインストールする必要があります。そのとき、端末に送ったゲームとセーブも消えます。\n" +
				"続けるなら、先に「セーブを PC に保存」で控えてから、端末の設定でアンインストールしてください。");
		if (r.Output.Contains("INSTALL_FAILED_VERSION_DOWNGRADE"))
			throw new AdbException("端末に入っている andEmuera のほうが新しい版です。古い版は入れられません。");
		if (r.Output.Contains("INSTALL_FAILED_INSUFFICIENT_STORAGE"))
			throw new AdbException("端末の空き容量が足りません。");
		if (r.ExitCode != 0 || !r.Output.Contains("Success"))
			throw new AdbException("インストールに失敗しました:\n" + r.Output.Trim());
	}

	/// <summary>andEmuera を (止めてから) 起動する。送ったゲームを読み直させるため。</summary>
	public static async Task LaunchAsync(Adb adb, CancellationToken ct = default)
	{
		await adb.ShellAsync($"am force-stop {AppId}", ct);
		await adb.ShellAsync($"monkey -p {AppId} -c android.intent.category.LAUNCHER 1", ct);
	}

	[GeneratedRegex(@"versionCode=(\d+)")] private static partial Regex VersionCodeRegex();
	[GeneratedRegex(@"versionName=(\S+)")] private static partial Regex VersionNameRegex();
	[GeneratedRegex(@"pkgFlags=\[[^\]]*DEBUGGABLE")] private static partial Regex DebuggableRegex();
}
