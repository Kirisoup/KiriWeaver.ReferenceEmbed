namespace KiriWeaver.ReferenceEmbed;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class EmbedConfigAttribute(
	bool compress = false,
	string? prefix = null
) : Attribute
{
	public bool DefaultCompress { get; } = compress;
	public string? Prefix { get; } = prefix;
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class EmbedIncludeAllAttribute : Attribute;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class EmbedIncludeAttribute : Attribute
{
	public string FileName { get; }
	public bool? Compress { get; }

	public EmbedIncludeAttribute(string fileName) => FileName = fileName;
	public EmbedIncludeAttribute(string fileName, bool compress) => 
		(FileName, Compress) = (fileName, compress);
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class EmbedExcludeAttribute(string fileName) : Attribute
{
	public string FileName { get; } = fileName;
}