using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using AssemblyUnhollower;
using Cpp2IL.Core;
using HarmonyLib;
using Il2CppDumper;
using Mono.Cecil;
using UnhollowerBaseLib;
using AssemblyUnhollowerRunner = AssemblyUnhollower.Program;

namespace CkBepInExRecovery
{
	public partial class Form1 : Form
	{
		private Task asyncRecovery;
		public Form1()
		{
			InitializeComponent();
		}

		private void button1_Click(object sender, EventArgs e)
		{
			button1.Enabled = false;
			toolStripStatusLabel1.Text = "実行中…";
			asyncRecovery = Task.Run(async () =>
			{
				Invoke(() => richTextBox1.AppendText("バージョン情報読み取り中…\n"));
				var verInfo = FileVersionInfo.GetVersionInfo(GAME_PATH).FileVersion.Split(".");
				var ver = new Version(int.Parse(verInfo[0]), int.Parse(verInfo[1]), int.Parse(verInfo[2]));

				Invoke(() => richTextBox1.AppendText("unity-libs 初期化中\n"));

				Directory.CreateDirectory(UNITY_LIBS_DIR);
				Directory.EnumerateFiles(UNITY_LIBS_DIR, "*.dll").Do(File.Delete);

				Invoke(() => richTextBox1.AppendText("unity-libs ダウンロード中\n"));

				using var httpClient = new HttpClient();
				using var zipStream = httpClient.GetStreamAsync($"http://unity.bepinex.dev/libraries/{ver}.zip").GetAwaiter().GetResult();
				using var zipArchive = new ZipArchive(zipStream, ZipArchiveMode.Read);

				zipArchive.ExtractToDirectory(UNITY_LIBS_DIR);

				Invoke(() => richTextBox1.AppendText("Il2Cppアセンブリダンプ中\n"));

				Directory.CreateDirectory(UNHOLLOWED);
				Directory.EnumerateFiles(UNHOLLOWED, "*.dll").Do(File.Delete);

				Il2CppDumper.Il2CppDumper.Init(GAME_ASSEMBLY,
											   METADATA,
											   new Config
											   {
												   GenerateStruct = false,
												   GenerateDummyDll = true,
											   },
											   s => Console.WriteLine(s),
											   out var metadata,
											   out var il2Cpp);

				var executor = new Il2CppExecutor(metadata, il2Cpp);
				var dummy = new DummyAssemblyGenerator(executor, true);

				var unhollowerOptions = new UnhollowerOptions
				{
					GameAssemblyPath = GAME_ASSEMBLY,
					Source = dummy.Assemblies,
					OutputDir = UNHOLLOWED,
					UnityBaseLibsDir = UNITY_LIBS_DIR,
					NoCopyUnhollowerLibs = true,
					SystemLibrariesPath = MANAGED
				};

				LogSupport.InfoHandler += s => Invoke(() => richTextBox1.AppendText($"{s.Trim()}\n"));
				LogSupport.WarningHandler += s => Invoke(() => richTextBox1.AppendText($"{s.Trim()}\n"));
				LogSupport.TraceHandler += s => Invoke(() => richTextBox1.AppendText($"{s.Trim()}\n"));
				LogSupport.ErrorHandler += s => Invoke(() => richTextBox1.AppendText($"{s.Trim()}\n"));

				try
				{
					AssemblyUnhollowerRunner.Main(unhollowerOptions);
				}
				catch (Exception e)
				{
					Invoke(() => richTextBox1.AppendText($"Exception while unhollowing: {e}\n"));
					Invoke(() => button1.Enabled = true);
					Invoke(() => toolStripStatusLabel1.Text = "エラーが発生しました\n");
					return;
				}

				File.WriteAllText(HASH_PATH, ComputeHash());

				Invoke(() => richTextBox1.AppendText("unhollowed unityダウンロード中\n"));

				using var unuhZipStream = httpClient.GetStreamAsync($"https://github.com/azutake/ckjp-unhollowed/releases/download/v{ver}/unhollowed-unityengine.zip").GetAwaiter().GetResult();
				using var unuhZipArchive = new ZipArchive(unuhZipStream, ZipArchiveMode.Read);

				unuhZipArchive.ExtractToDirectory(UNHOLLOWED, true);

				Invoke(() => button1.Enabled = true);
				Invoke(() => toolStripStatusLabel1.Text = "完了");
			});
		}

		const string GAME_NAME = "CoreKeeper";
		const string GAME_PATH = GAME_NAME + ".exe";
		const string UNITY_LIBS_DIR = "BepInEx\\unity-libs";
		const string HASH_PATH = "BepInEx\\unhollowed\\assembly-hash.txt";
		const string GAME_ASSEMBLY = "GameAssembly.dll";
		const string UNHOLLOWED = "BepInEx\\unhollowed";
		const string MANAGED = "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Core Keeper\\mono\\Managed";
		const string METADATA = GAME_NAME + "_Data\\il2cpp_data\\Metadata\\global-metadata.dat";

		string ByteArrayToString(byte[] data)
		{
			var builder = new StringBuilder(data.Length * 2);

			foreach (var b in data)
				builder.AppendFormat("{0:x2}", b);

			return builder.ToString();
		}

		string ComputeHash()
		{
			using var md5 = MD5.Create();

			static void HashFile(ICryptoTransform hash, string file)
			{
				const int defaultCopyBufferSize = 81920;
				using var fs = File.OpenRead(file);
				var buffer = new byte[defaultCopyBufferSize];
				int read;
				while ((read = fs.Read(buffer)) > 0)
					hash.TransformBlock(buffer, 0, read, buffer, 0);
			}

			static void HashString(ICryptoTransform hash, string str)
			{
				var buffer = Encoding.UTF8.GetBytes(str);
				hash.TransformBlock(buffer, 0, buffer.Length, buffer, 0);
			}

			HashFile(md5, GAME_ASSEMBLY);

			if (Directory.Exists(UNITY_LIBS_DIR))
				foreach (var file in Directory.EnumerateFiles(UNITY_LIBS_DIR, "*.dll",
															  SearchOption.TopDirectoryOnly))
				{
					HashString(md5, Path.GetFileName(file));
					HashFile(md5, file);
				}

			// Hash some common dependencies as they can affect output
			HashString(md5, typeof(AssemblyUnhollowerRunner).Assembly.GetName().Version.ToString());
			HashString(md5, typeof(Cpp2IlApi).Assembly.GetName().Version.ToString());
			HashString(md5, typeof(Il2CppDumper.Il2CppDumper).Assembly.GetName().Version.ToString());

			md5.TransformFinalBlock(new byte[0], 0, 0);

			return ByteArrayToString(md5.Hash);
		}
	}
}