using System.Text;
using System.Text.Json;

namespace AndEmuera.Installer;

/// <summary>
/// かんたん転送の覚え書き。端末のゲーム名 → それを送った PC のフォルダ、と最後に開いた場所。
/// 置き場所は %LOCALAPPDATA%\andEmuera\installer-settings.json。exe の隣に置くと、
/// 自己更新で入れ替えたり zip を展開し直したりしたときに消えるため。
/// 読めない・書けないときは黙って空のまま動く (覚えていないだけで、送る操作には影響しない)。
/// </summary>
sealed class Settings
{
	/// <summary>ゲーム名 (端末でのフォルダ名) → PC で選んだフォルダ。ゲーム名は端末と同じく大文字小文字を区別する。</summary>
	public Dictionary<string, string> GameFolders { get; set; } = new(StringComparer.Ordinal);

	/// <summary>フォルダ選択画面を最後に開いた場所 (選んだフォルダの親)。</summary>
	public string? LastFolder { get; set; }

	public static string FilePath { get; set; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "andEmuera", "installer-settings.json");

	static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

	public static Settings Load()
	{
		try
		{
			if (!File.Exists(FilePath)) return new();
			var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath, Encoding.UTF8), Json) ?? new();
			// 逆シリアル化した辞書は既定の比較子になるので、念のため作り直す
			s.GameFolders = new Dictionary<string, string>(s.GameFolders ?? [], StringComparer.Ordinal);
			return s;
		}
		catch
		{
			return new();
		}
	}

	/// <summary>保存する。書きかけで落ちても壊れないよう、一時ファイルに書いてから置き換える。</summary>
	public void Save()
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
			string tmp = FilePath + ".tmp";
			File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));
			File.Move(tmp, FilePath, overwrite: true);
		}
		catch
		{
		}
	}

	public string? FolderOf(string gameName) => GameFolders.TryGetValue(gameName, out var f) ? f : null;
}
