using System;
using System.IO;
using System.Text;
using QuietReader;

class CoreTests
{
    static int passed;
    static void Assert(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    static int Main()
    {
        try { Run(); return 0; }
        catch (Exception ex) { Console.WriteLine("ERROR TYPE: " + ex.GetType().FullName); Console.WriteLine("ERROR MESSAGE: " + ex.Message); if (ex.InnerException != null) Console.WriteLine("INNER: " + ex.InnerException.Message); return 1; }
    }
    static void Run()
    {
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string label, text = "第一章 风起\n中文阅读测试𠀀。\n第二章 归途\nChapter 3 Home\n结尾";
            Assert(TextDecoder.Decode(Encoding.UTF8.GetBytes(text), out label) == text && label == "UTF-8", "UTF-8 supplementary characters");
            byte[] bom = Encoding.UTF8.GetPreamble();
            byte[] body = Encoding.UTF8.GetBytes(text);
            byte[] withBom = new byte[bom.Length + body.Length]; bom.CopyTo(withBom, 0); body.CopyTo(withBom, bom.Length);
            Assert(TextDecoder.Decode(withBom, out label) == text, "UTF-8 BOM removed");
            var gb = Encoding.GetEncoding("GB18030");
            Assert(TextDecoder.Decode(gb.GetBytes(text), out label) == text && label.StartsWith("GB"), "GB18030 including GBK and supplementary characters");
            foreach (var enc in new Encoding[] { new UnicodeEncoding(false, true), new UnicodeEncoding(true, true), new UTF32Encoding(false, true), new UTF32Encoding(true, true) })
            {
                bom = enc.GetPreamble(); body = enc.GetBytes(text); withBom = new byte[bom.Length + body.Length]; bom.CopyTo(withBom, 0); body.CopyTo(withBom, bom.Length);
                Assert(TextDecoder.Decode(withBom, out label) == text, enc.WebName + " BOM roundtrip");
            }
            Assert(TextDecoder.Decode(Encoding.UTF8.GetBytes("a\r\nb\rc"), out label) == "a\nb\nc", "newline normalization");
            bool rejected = false;
            try { TextDecoder.Decode(new byte[0], out label); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "empty input rejected");
            var chapters = TextDecoder.Chapters(text);
            Assert(chapters.Count == 3 && text.Substring(chapters[1].Position).StartsWith("第二章"), "Chinese and English chapters use exact character offsets");
            Assert(TextDecoder.Chapters("普通文本，没有章节。\n依然可以阅读。").Count == 0, "chapterless text accepted");
            var longText = new StringBuilder();
            while (longText.Length < 100000) longText.Append("中文长篇测试𠀀，检查连续阅读。\n");
            string novel = longText.ToString();
            foreach (int anchor in new[] { 0, 8192, 16000, 40000, novel.Length - 5, novel.Length })
            {
                var window = new TextWindow(novel, anchor);
                Assert(window.Text.Length <= TextWindow.Capacity && window.Start <= anchor && window.End >= anchor && novel.Substring(window.Start, window.Text.Length) == window.Text,
                    "bounded text window covers absolute anchor " + anchor);
                Assert(!Char.IsLowSurrogate(window.Text[0]) && !Char.IsHighSurrogate(window.Text[window.Text.Length - 1]), "text-window boundaries preserve Unicode pairs " + anchor);
            }
            string singleLine = new string('字', 50000);
            Assert(new TextWindow(singleLine, 42000).Text.Length <= TextWindow.Capacity, "single-line novels also have bounded layout");
            var store = new LibraryStore(Path.Combine(root, "library"));
            Assert(!store.State.Preferences.Topmost, "always-on-top disabled by default");
            string path = Path.Combine(root, "sample.txt"); File.WriteAllText(path, text, new UTF8Encoding(false));
            var imported = store.PrepareImport(path); var book = store.Add(imported);
            book.Position = 17; book.Progress = .35; book.LastRead = DateTime.UtcNow.ToString("o");
            store.State.CurrentId = book.Id; store.State.Preferences.Background = "#112233"; store.State.Preferences.Width = 510;
            store.Save();
            var reopened = new LibraryStore(store.Root);
            Assert(reopened.State.Books[0].Position == 17 && reopened.State.CurrentId == book.Id, "per-book position survives restart");
            Assert(reopened.State.Preferences.Background == "#112233" && reopened.State.Preferences.Width == 510, "window and theme preferences survive restart");
            Assert(reopened.ReadBook(book) == text, "imported local copy readable");
            var duplicate = reopened.Add(reopened.PrepareImport(path));
            Assert(reopened.State.Books.Count == 1 && duplicate.Position == 17, "reimport preserves progress without duplicate");
            string moved = Path.Combine(root, "renamed.txt"); File.Move(path, moved);
            Assert(reopened.Add(reopened.PrepareImport(moved)).Id == book.Id && reopened.ReadBook(book) == text, "moved or renamed source keeps identity");
            File.WriteAllText(moved, text + "\n新的结尾");
            Assert(reopened.Add(reopened.PrepareImport(moved)).Id != book.Id && reopened.State.Books.Count == 2, "different content stored separately");
            reopened.Save(); File.WriteAllText(Path.Combine(store.Root, "state.json"), "broken json");
            var recovered = new LibraryStore(store.Root);
            Assert(recovered.State.Books.Count == 1 && recovered.RecoveryNotice != null, "corrupt state recovers atomic backup");
            recovered.State.Version = 1; recovered.State.Preferences.Background = "#F7F5EF"; recovered.Save();
            var migrated = new LibraryStore(recovered.Root);
            Assert(migrated.State.Version == 2 && migrated.State.Preferences.Background == "#1E1E1E" && migrated.State.Books[0].Position == 17,
                "upgrade applies VS Code dark theme without losing reading progress");
            bool unsafeId = false; try { store.BookPath("../bad"); } catch (InvalidDataException) { unsafeId = true; }
            Assert(unsafeId, "invalid persisted path identity rejected");
            File.WriteAllText(Path.Combine(root, "fake.pdf"), text);
            rejected = false; try { store.PrepareImport(Path.Combine(root, "fake.pdf")); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "non-TXT extension rejected");
            string large = Path.Combine(root, "large.txt"); using (var fs = File.Create(large)) fs.SetLength(32 * 1024 * 1024 + 1);
            rejected = false; try { store.PrepareImport(large); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "size limit checked before allocation");
            Console.WriteLine("All " + passed + " core checks passed.");
        }
        finally { Directory.Delete(root, true); }
    }
}
