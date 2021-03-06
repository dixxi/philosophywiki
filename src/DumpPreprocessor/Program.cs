﻿using Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml;

namespace DumpPreprocessor
{
	class Program
	{
		private static int threadCount;
		private static Semaphore linkWrites;

		private static long totalTitles = 0;
		private static long totalLinks = 0;

		static void Main(string[] args)
		{
			threadCount = Environment.ProcessorCount;
			linkWrites = new Semaphore(threadCount, threadCount);

			Console.WriteLine("Wikipedia dump preprocessor");

			if (args.Length < 1)
			{
				Console.WriteLine("Please specify the input file stem.");
				return;
			}

			string dumpFile = args[0] + ".xml";
			string linkFile = args[0] + ".links.txt";
			string titleFile = args[0] + ".titles.txt";
			string metaFile = args[0] + ".meta.txt";

			using (var stream = new FileStream(dumpFile, FileMode.Open, FileAccess.Read))
			using (var reader = XmlReader.Create(stream))
			using (var linkWriter = new StreamWriter(linkFile, false, Encoding.Unicode))
			using (var pageWriter = new StreamWriter(titleFile, false, Encoding.Unicode))
			using (var metaWriter = new StreamWriter(metaFile, false, Encoding.Unicode))
			{
				Stopwatch sw = Stopwatch.StartNew();
				reader.MoveToContent();

				RawMeta meta = null;
				RawPage page = null;
				bool inRevision = false;

				while (reader.Read())
				{
					Utils.UpdateProgress(stream);

					if (reader.NodeType == XmlNodeType.Element)
					{
						if (reader.Name == "page")
							page = new RawPage();
						else if (page != null && reader.Name == "title")
							page.Title = reader.ReadElementContentAsString();
						else if (page != null && reader.Name == "ns")
							page.NamespaceId = reader.ReadElementContentAsInt();
						else if (page != null && !inRevision && reader.Name == "id")
							page.Id = reader.ReadElementContentAsInt();
						else if (page != null && reader.Name == "text")
							page.Text = reader.ReadElementContentAsString();
						else if (page != null && reader.Name == "revision")
							inRevision = true;
						else if (reader.Name == "siteinfo")
							meta = new RawMeta();
						else if (meta != null && reader.Name == "dbname")
							meta.DatabaseName = reader.ReadElementContentAsString();
						else if (meta != null && reader.Name == "generator")
							meta.Generator = reader.ReadElementContentAsString();
						else if (meta != null && reader.Name == "namespaces")
							meta.Namespaces = new Dictionary<int, string>();
						else if (meta != null && meta.Namespaces != null && reader.Name == "namespace")
						{
							int ns = int.Parse(reader.GetAttribute("key"));
							string name = reader.ReadElementContentAsString();
							meta.Namespaces.Add(ns, name);
						}
					}
					else if (reader.NodeType == XmlNodeType.EndElement)
					{
						if (reader.Name == "page")
						{
							page.Text = page.Text.Replace('\n', ' ');
							page.Text = page.Text.Replace('\t', ' ');
							WritePage(page, pageWriter);
							WriteLinksAsync(page, linkWriter);
							page = null;
						}
						else if (page != null && reader.Name == "revision")
							inRevision = false;
						else if (reader.Name == "siteinfo")
						{
							WriteMeta(meta, metaWriter);
							meta = null;
						}

					}
				}

				// all write slots should be in use by now (now task can starve now)
				// in the end, aquire all write slots back
				// if this is successfull, all writing taks should have completed
				for (int i = 0; i < threadCount; i++)
					linkWrites.WaitOne();

				sw.Stop();

				metaWriter.WriteLine("TotalTitles: " + totalTitles);
				metaWriter.WriteLine("TotalLinks: " + totalLinks);
				metaWriter.WriteLine("Finished after: " + sw.Elapsed);

				Console.WriteLine();
				Console.WriteLine("Finished after " + sw.Elapsed);
			}
		}

		private static void WriteMeta(RawMeta meta, TextWriter writer)
		{
			writer.WriteLine("File created: " + DateTime.Now);
			writer.WriteLine("Database: " + meta.DatabaseName);
			writer.WriteLine("Generator: " + meta.Generator);
		}

		private static void WritePage(RawPage page, TextWriter writer)
		{
			Interlocked.Increment(ref totalTitles);
			writer.WriteLine(page.Id);
			writer.WriteLine(page.Title);
			writer.WriteLine(CanonicalPageName(page.Title));
			writer.WriteLine(page.Text.Length);

			// UTF16 surrogates
			// substring may split the string at a surrogate in which case an Encoder in writer.WriteLine fails. EncoderFallback has to specified!
			var t = page.Text.Substring(0, Math.Min(100, page.Text.Length));
			writer.WriteLine(t);
		}

		// does not implement the reverse pipe trick
		private static Regex linkRegex = new Regex(@"\[\[([^#|]+?)(#.*?)?(\|.*?)?\]\]", RegexOptions.Compiled);
		private static Regex removeRegex1 = new Regex(@"\{\{[^\{\}]+\}\}", RegexOptions.Compiled);
		private static Regex removeRegex2 = new Regex(@"\([^\(\)]+?\)", RegexOptions.Compiled);

		private static void WriteLinksAsync(RawPage page, TextWriter writer)
		{
			// skip all pages which are not in the main/article namespace
			// see: http://en.wikipedia.org/wiki/Wikipedia:Namespace
			if (page.NamespaceId != 0)
				return;

			linkWrites.WaitOne(); // wait for write slot

			Task.Run(() =>
			{
				try
				{
					// prepare page text to find first link
					var rawText = page.Text;
					while(true) {
						var oldLength = rawText.Length;
						rawText = removeRegex1.Replace(rawText, string.Empty);
						rawText = removeRegex2.Replace(rawText, string.Empty);
						if (oldLength == rawText.Length)
							break;
					}

					var firstLinks = linkRegex
						.Matches(rawText)
						.Cast<Match>()
						.Select(m => m.Groups[1].Value)
						.Select(l => CanonicalPageName(l))
						.Where(l => l.Length > 0) // yes, there are wikipedia users who put empty links in their articles ...
						.Where(l => l.Length < 300) // sanity check
						.GetEnumerator();

					string firstLink = "Bild:";
					while(firstLinks.MoveNext() && firstLink.StartsWith("Bild:"))
						firstLink = firstLinks.Current;
					if (firstLink.StartsWith("Bild:"))
					{
						//Console.WriteLine("Warning: Could not find a first link for " + page.Title);
						firstLink = "";
					}

					// find links using regex and make them unique (this is expensive and can take half an hour !!!)
					var links = linkRegex
						.Matches(page.Text)
						.Cast<Match>()
						.Select(m => m.Groups[1].Value)
						.Select(l => CanonicalPageName(l))
						.Where(l => l.Length > 0) // yes, there are wikipedia users who put empty links in their articles ...
						.Where(l => l.Length < 300) // sanity check
						.Distinct()
						.ToArray(); // evaluate eager to keep locked region as short as possible

					Interlocked.Add(ref totalLinks, links.Length);

					lock (writer)
					{
						writer.WriteLine(page.Id);
						writer.WriteLine(firstLink);
						foreach (var l in links)
							if (l.Contains('|'))
								Console.WriteLine("Fatal: link " + l + " on page " + page.Title + " contains |");
						writer.WriteLine(string.Join("|", links));
					}
				}
				catch (Exception e)
				{
					Console.WriteLine("Write task failed: " + e);
					throw;
				}
				finally
				{
					linkWrites.Release(); // release a write slot
				}

			});
		}

		private static string CanonicalPageName(string link)
		{
			string l = link;
			l = l.Replace(' ', '_'); // space and underscore are equivalent
			l = l.Trim('_'); // trim spaces and underscores at the start and end
			l = HttpUtility.HtmlDecode(l); // decode html entities
			if (l.Length > 0 && char.IsLower(l[0])) // ensure first character is upper case
				l = char.ToUpper(l[0]) + l.Substring(1);

			return l;
		}
	}
}
