using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using KiriWeaver.ReferenceEmbed;

[assembly: EmbedConfig(defaultCompress: true, prefix: nameof(TestProject))]
[assembly: EmbedInclude("Mono.Cecil")]

namespace TestProject;

public static class VeryRandomClass
{
	public static void Main() {
		Console.WriteLine($"resources: {Assembly.GetExecutingAssembly().GetManifestResourceNames()
			.Aggregate("", (names, name) => names + name + ", ")}");

		try {
			using var resource = Assembly.GetExecutingAssembly()
				.GetManifestResourceStream($"{nameof(TestProject)}.TestProject.compressed");
			using var decompress = new DeflateStream(resource, CompressionMode.Decompress);
			using var result = new MemoryStream();

			decompress.CopyTo(result);
			Console.WriteLine($"decompressed assembly: {Assembly.Load(result.ToArray()).GetName()}");
		} catch (Exception ex) {
			Console.WriteLine(ex);
		}
	}
}