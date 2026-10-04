using System.IO.Compression;
using System.Text;

namespace AndEmuera.Installer;

/// <summary>
/// APK の AndroidManifest.xml (バイナリ XML) から、入れる前に確かめたいことだけを読む。
/// adb に任せて失敗させると、adb が自分でアンインストールして入れ直すことがあり
/// (古い Debug APK で実際に起きた)、端末のゲームとセーブが消える。だから危ないものはこちらで先に止める。
/// </summary>
sealed record ApkInfo(string Package, long VersionCode, string VersionName, bool Debuggable, bool HasCode)
{
	const int ResVersionCode = 0x0101021b;
	const int ResVersionName = 0x0101021c;
	const int ResDebuggable = 0x0101000f;

	public static ApkInfo Read(string apkPath)
	{
		using var zip = ZipFile.OpenRead(apkPath);
		var entry = zip.GetEntry("AndroidManifest.xml") ?? throw new InvalidDataException("APK の中に AndroidManifest.xml がありません。");
		byte[] xml;
		using (var s = entry.Open())
		using (var ms = new MemoryStream())
		{
			s.CopyTo(ms);
			xml = ms.ToArray();
		}

		// Debug ビルドは Fast Deployment でアセンブリを APK の外に置くことがあり、
		// そういう APK を入れると成功してもコードは古いまま動く
		bool hasCode = zip.Entries.Any(e =>
			e.FullName.StartsWith("assemblies/") ||
			(e.FullName.StartsWith("lib/") &&
			 (e.Name == "libassembly-store.so" || e.Name == "lib_andEmuera.Android.dll.so")));

		var (package, code, name, debuggable) = ParseManifest(xml);
		return new ApkInfo(package, code, name, debuggable, hasCode);
	}

	/// <summary>
	/// バイナリ XML を頭から読む。見るのは文字列プール・リソース ID 表・開始タグだけ。
	/// 属性名は難読化で空になっていることがあるので、名前とリソース ID の両方で判定する。
	/// </summary>
	static (string Package, long VersionCode, string VersionName, bool Debuggable) ParseManifest(byte[] b)
	{
		string[] strings = [];
		int[] resIds = [];
		string package = "";
		long versionCode = -1;
		string versionName = "";
		bool debuggable = false;

		int pos = 8; // ファイル全体のヘッダ (RES_XML_TYPE)
		while (pos + 8 <= b.Length)
		{
			int type = BitConverter.ToUInt16(b, pos);
			int headerSize = BitConverter.ToUInt16(b, pos + 2);
			int size = BitConverter.ToInt32(b, pos + 4);
			if (size <= 0) break;

			switch (type)
			{
				case 0x0001: // 文字列プール
					strings = ReadStringPool(b, pos);
					break;
				case 0x0180: // リソース ID 表 (文字列番号 → 属性のリソース ID)
					resIds = new int[(size - headerSize) / 4];
					for (int i = 0; i < resIds.Length; i++) resIds[i] = BitConverter.ToInt32(b, pos + headerSize + i * 4);
					break;
				case 0x0102: // 開始タグ
				{
					int ext = pos + headerSize;
					string tag = Str(strings, BitConverter.ToInt32(b, ext + 4));
					int attrStart = BitConverter.ToUInt16(b, ext + 8);
					int attrSize = BitConverter.ToUInt16(b, ext + 10);
					int attrCount = BitConverter.ToUInt16(b, ext + 12);
					for (int i = 0; i < attrCount; i++)
					{
						int a = ext + attrStart + i * attrSize;
						int nameIdx = BitConverter.ToInt32(b, a + 4);
						int raw = BitConverter.ToInt32(b, a + 8);
						byte dataType = b[a + 15];
						int data = BitConverter.ToInt32(b, a + 16);
						string attr = Str(strings, nameIdx);
						int resId = nameIdx >= 0 && nameIdx < resIds.Length ? resIds[nameIdx] : 0;
						string value = raw >= 0 ? Str(strings, raw) : dataType == 0x03 ? Str(strings, data) : "";

						if (tag == "manifest" && attr == "package") package = value;
						else if (tag == "manifest" && (resId == ResVersionCode || attr == "versionCode")) versionCode = (uint)data;
						else if (tag == "manifest" && (resId == ResVersionName || attr == "versionName")) versionName = value;
						else if (tag == "application" && (resId == ResDebuggable || attr == "debuggable")) debuggable = data != 0;
					}
					break;
				}
			}
			pos += size;
		}
		if (package.Length == 0 || versionCode < 0)
			throw new InvalidDataException("APK の版を読み取れませんでした。");
		return (package, versionCode, versionName, debuggable);
	}

	static string Str(string[] pool, int i) => i >= 0 && i < pool.Length ? pool[i] : "";

	static string[] ReadStringPool(byte[] b, int pos)
	{
		int headerSize = BitConverter.ToUInt16(b, pos + 2);
		int count = BitConverter.ToInt32(b, pos + 8);
		int flags = BitConverter.ToInt32(b, pos + 16);
		int stringsStart = BitConverter.ToInt32(b, pos + 20);
		bool utf8 = (flags & 0x100) != 0;
		var result = new string[count];
		for (int i = 0; i < count; i++)
		{
			int off = pos + stringsStart + BitConverter.ToInt32(b, pos + headerSize + i * 4);
			if (utf8)
			{
				// UTF-16 での長さ (1〜2 バイト) → UTF-8 でのバイト数 (1〜2 バイト) → 本体
				off += (b[off] & 0x80) != 0 ? 2 : 1;
				int len = b[off];
				if ((len & 0x80) != 0) { len = ((len & 0x7f) << 8) | b[off + 1]; off += 2; } else off += 1;
				result[i] = Encoding.UTF8.GetString(b, off, len);
			}
			else
			{
				int len = BitConverter.ToUInt16(b, off);
				if ((len & 0x8000) != 0) { len = ((len & 0x7fff) << 16) | BitConverter.ToUInt16(b, off + 2); off += 4; } else off += 2;
				result[i] = Encoding.Unicode.GetString(b, off, len * 2);
			}
		}
		return result;
	}
}
