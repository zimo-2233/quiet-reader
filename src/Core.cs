using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace QuietReader
{
    public class Book : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Encoding { get; set; }
        public int Position { get; set; }
        public int Length { get; set; }
        public string LastRead { get; set; }
        double progress;
        public double Progress
        {
            get { return progress; }
            set { progress = value; var changed = PropertyChanged; if (changed != null) changed(this, new PropertyChangedEventArgs("Progress")); }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }
    public class Preferences
    {
        public string Background { get; set; }
        public string Foreground { get; set; }
        public double FontSize { get; set; }
        public double Opacity { get; set; }
        public bool Topmost { get; set; }
        public bool Compact { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public Preferences()
        {
            Background = "#1E1E1E"; Foreground = "#D4D4D4"; FontSize = 19;
            Opacity = 1; Topmost = false; Width = 960; Height = 680;
            Left = -99999; Top = -99999;
        }
    }
    public class LibraryState
    {
        public int Version { get; set; }
        public string CurrentId { get; set; }
        public Preferences Preferences { get; set; }
        public List<Book> Books { get; set; }
        public LibraryState() { Version = 2; Preferences = new Preferences(); Books = new List<Book>(); }
    }
    public class Chapter
    {
        public string Title { get; set; }
        public int Position { get; set; }
        public override string ToString() { return Title; }
    }
    public class ImportedBook
    {
        public string Id;
        public string Title;
        public string Text;
        public string Encoding;
    }
    // Bound the amount WPF has to format, regardless of the novel's total length.
    public class TextWindow
    {
        public const int Capacity = 16384;
        public int Start { get; private set; }
        public string Text { get; private set; }
        public int End { get { return Start + Text.Length; } }
        public TextWindow(string document, int anchor)
        {
            anchor = Math.Max(0, Math.Min(anchor, document.Length));
            int start = Math.Max(0, Math.Min(anchor - 4096, document.Length - Capacity));
            // Prefer paragraph boundaries, but also bound single-line novels.
            if (start > 0)
            {
                int newline = document.LastIndexOf('\n', start, Math.Min(start + 1, 256));
                if (newline >= 0) start = newline + 1;
                if (start < document.Length && Char.IsLowSurrogate(document[start])) start++;
            }
            int end = Math.Min(document.Length, start + Capacity);
            if (end < anchor)
            {
                start = Math.Max(0, anchor - Capacity);
                if (start < document.Length && Char.IsLowSurrogate(document[start])) start++;
                end = Math.Min(document.Length, start + Capacity);
            }
            if (end < document.Length && end > start && Char.IsHighSurrogate(document[end - 1])) end--;
            Start = start; Text = document.Substring(start, end - start);
        }
    }
    public static class TextDecoder
    {
        public static string Decode(byte[] bytes, out string name)
        {
            string text;
            if (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0)
            { name = "UTF-32 LE"; text = new UTF32Encoding(false, true, true).GetString(bytes, 4, bytes.Length - 4); }
            else if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff)
            { name = "UTF-32 BE"; text = new UTF32Encoding(true, true, true).GetString(bytes, 4, bytes.Length - 4); }
            else if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
            { name = "UTF-8"; text = new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3); }
            else if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
            { name = "UTF-16 LE"; text = new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2); }
            else if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
            { name = "UTF-16 BE"; text = new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2); }
            else
            {
                // A UTF-16 file without a BOM can otherwise look like valid UTF-8 with NULs.
                int even = 0, odd = 0, pairs = Math.Min(bytes.Length / 2, 2048);
                for (int i = 0; i < pairs; i++) { if (bytes[2 * i] == 0) even++; if (bytes[2 * i + 1] == 0) odd++; }
                if (pairs > 4 && odd > pairs / 4 && even < pairs / 10)
                { name = "UTF-16 LE"; text = new UnicodeEncoding(false, false, true).GetString(bytes); }
                else if (pairs > 4 && even > pairs / 4 && odd < pairs / 10)
                { name = "UTF-16 BE"; text = new UnicodeEncoding(true, false, true).GetString(bytes); }
                else
                {
                    try { text = new UTF8Encoding(false, true).GetString(bytes); name = "UTF-8"; }
                    catch (DecoderFallbackException)
                    {
                        text = Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes);
                        name = "GB18030 / GBK";
                    }
                }
            }
            if (text.IndexOf('\0') >= 0) throw new InvalidDataException("文件包含二进制内容，请选择纯文本 TXT 文件。");
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (String.IsNullOrWhiteSpace(text)) throw new InvalidDataException("这个 TXT 文件没有可阅读的正文。");
            return text;
        }
        public static string Hash(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
        public static List<Chapter> Chapters(string text)
        {
            var chapters = new List<Chapter>();
            var pattern = new Regex(@"^\s*(?:第[零〇一二三四五六七八九十百千万两\d]+[章回卷节部篇]|(?:Chapter|CHAPTER|chapter)\s+\d+|序章|序言|楔子|尾声|后记)(?:[^\n]{0,90})$", RegexOptions.CultureInvariant);
            int start = 0;
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length <= 110 && pattern.IsMatch(line)) chapters.Add(new Chapter { Title = line.Trim(), Position = start });
                    start += line.Length + 1;
                    if (chapters.Count >= 10000) break;
                }
            }
            return chapters;
        }
    }
    public class LibraryStore
    {
        public readonly string Root;
        public LibraryState State;
        public string RecoveryNotice;
        public LibraryStore(string root)
        {
            Root = Path.GetFullPath(root);
            Directory.CreateDirectory(Path.Combine(Root, "books"));
            State = ReadState(Path.Combine(Root, "state.json"));
            if (State == null && File.Exists(Path.Combine(Root, "state.json")))
            {
                State = ReadState(Path.Combine(Root, "state.json.bak"));
                RecoveryNotice = State == null ? "阅读记录损坏，已保留原文件；导入同一本书可继续阅读。" : "已从备份恢复阅读记录。";
            }
            if (State == null) State = new LibraryState();
            if (State.Preferences == null) State.Preferences = new Preferences();
            if (State.Version < 2)
            {
                State.Preferences.Background = "#1E1E1E";
                State.Preferences.Foreground = "#D4D4D4";
                State.Version = 2;
            }
            if (State.Books == null) State.Books = new List<Book>();
            State.Books = State.Books.Where(b => b != null && ValidId(b.Id)).GroupBy(b => b.Id).Select(g => g.First()).ToList();
        }
        private LibraryState ReadState(string path)
        {
            try { return new JavaScriptSerializer().Deserialize<LibraryState>(File.ReadAllText(path, Encoding.UTF8)); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return null; }
        }
        public static bool ValidId(string id) { return id != null && Regex.IsMatch(id, "^[a-f0-9]{64}$"); }
        public string BookPath(string id)
        {
            if (!ValidId(id)) throw new InvalidDataException("无效的书籍标识。");
            return Path.Combine(Root, "books", id + ".txt");
        }
        public ImportedBook PrepareImport(string path)
        {
            if (!String.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("请选择 .txt 小说或纯文本文件。");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > 32 * 1024 * 1024) throw new InvalidDataException("目前支持 32 MB 以内的 TXT 文件，请先拆分较大的文件。");
                var bytes = new byte[(int)stream.Length];
                int offset = 0, count;
                while (offset < bytes.Length && (count = stream.Read(bytes, offset, bytes.Length - offset)) > 0) offset += count;
                string encoding;
                string text = TextDecoder.Decode(bytes, out encoding);
                string id = TextDecoder.Hash(text);
                if (!File.Exists(BookPath(id))) AtomicWrite(BookPath(id), text, false);
                return new ImportedBook { Id = id, Title = Path.GetFileNameWithoutExtension(path), Text = text, Encoding = encoding };
            }
        }
        public Book Add(ImportedBook imported)
        {
            var book = State.Books.Find(b => b.Id == imported.Id);
            if (book == null)
            {
                book = new Book { Id = imported.Id, Title = imported.Title, Encoding = imported.Encoding, Length = imported.Text.Length };
                State.Books.Add(book);
            }
            return book;
        }
        public string ReadBook(Book book) { return File.ReadAllText(BookPath(book.Id), Encoding.UTF8); }
        public void Save()
        {
            var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(State);
            AtomicWrite(Path.Combine(Root, "state.json"), json, true);
        }
        public static void AtomicWrite(string path, string text, bool backup)
        {
            string temp = path + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
                { writer.Write(text); writer.Flush(); fs.Flush(true); }
                if (File.Exists(path)) File.Replace(temp, path, backup ? path + ".bak" : null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
