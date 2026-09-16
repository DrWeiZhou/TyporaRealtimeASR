using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TyporaAsr;

/// <summary>Polished paragraphs of one window. Continues: the first paragraph continues the document's last paragraph (same main idea);
/// every other paragraph starts a new main idea (a new numbered item).</summary>
public sealed record PolishResult(string[] Paragraphs,bool Continues);

/// <summary>Request/response contract for window polishing. The user's prompt decides style; the contract asks the model to
/// decide whether the new text continues the last paragraph's main idea, marked with ContinueMark.</summary>
public static class PolishFormat {
 public const int MaxContextChars=600;
 /// <summary>What the model outputs when a window holds nothing worth recording.</summary>
 public const string Empty="（无）";
 public const string ContinueMark="【接续】";
 public static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
 public const string Contract="""
【输入与输出约定（程序自动追加，本约定优先于上文关于段落结构的要求）】
输入分两部分：<上文> 是文档中当前最后一个段落，仅用于理解和判断衔接，不要输出或改写；<待整理> 是本次需要整理的转写文本，只输出它整理后的正文。
每个段落只讲一个大意，段落第一句为该段主题或总结。请先判断 <待整理> 开头的内容与 <上文> 是否属于同一个大意、是紧接着的内容：
- 是：输出的第一个段落以“【接续】”开头，这一段只写接着上文的内容，不要重复上文，也不要再写主题句；
- 否（或 <上文> 为“（无）”）：不要写“【接续】”，直接开始新段落。
<待整理> 中后面出现的新大意各自另起一段。段落之间用一个空行分隔，段落内不要换行；不要输出标题、编号、列表符号或 <上文>、<待整理> 标记。
<待整理> 全部是寒暄、语气词或无效内容时，只输出：（无）
""";

 public static string SystemPrompt(string prompt)=>prompt.TrimEnd()+"\n\n"+Contract;

 public static string Input(string context,string text)=>
  new StringBuilder("<上文>\n").Append(context.Length>0?context:Empty).Append("\n</上文>\n\n<待整理>\n").Append(text).Append("\n</待整理>").ToString();

 /// <summary>Splits the model answer into paragraphs; no paragraphs means nothing should be inserted.
 /// A leading ContinueMark is honored only when there is a previous paragraph (hasContext).</summary>
 public static PolishResult Parse(string content,bool hasContext) {
  var text=content.Trim();
  if(text.StartsWith("```",StringComparison.Ordinal)){
   var firstLine=text.IndexOf('\n');text=firstLine<0?"":text[(firstLine+1)..];
   var fence=text.LastIndexOf("```",StringComparison.Ordinal);if(fence>=0)text=text[..fence];
  }
  text=Regex.Replace(text,@"</?(?:上文|待整理)>","").Trim();
  var continues=false;
  var mark=Regex.Match(text,@"^[【\[]\s*接续\s*[】\]]\s*");
  if(mark.Success){continues=hasContext;text=text[mark.Length..];}
  text=Regex.Replace(text,@"[【\[]\s*接续\s*[】\]]","");
  var paragraphs=Regex.Split(text,@"[\r\n]+").Select(Clean).Where(x=>x.Length>0&&!IsPlaceholder(x)).ToArray();
  return new(paragraphs,continues&&paragraphs.Length>0);
 }

 private static string Clean(string? value)=>Regex.Replace(value??"",@"\s*[\r\n]+\s*","").Trim();
 private static bool IsPlaceholder(string value)=>Regex.IsMatch(value,@"^[（(][^（）()]*[)）]$");

 public static string Serialize(PolishResult result)=>JsonSerializer.Serialize(new {paragraphs=result.Paragraphs,continues=result.Continues},Json);
 /// <summary>Reads a stored result. Results stored by the former topic format ({"blocks":[…]}) are flattened: topic titles become
 /// paragraphs of their own, and a leading "continue" block continues the previous paragraph.</summary>
 public static PolishResult Deserialize(string json){
  try{
   using var doc=JsonDocument.Parse(json);var root=doc.RootElement;var result=new List<string>();var continues=false;
   if(root.ValueKind!=JsonValueKind.Object)return new([],false);
   if(root.TryGetProperty("paragraphs",out var paragraphs)&&paragraphs.ValueKind==JsonValueKind.Array){
    Strings(paragraphs,result);
    continues=root.TryGetProperty("continues",out var c)&&c.ValueKind==JsonValueKind.True;
   }
   else if(root.TryGetProperty("blocks",out var blocks)&&blocks.ValueKind==JsonValueKind.Array)
    foreach(var block in blocks.EnumerateArray()){
     if(block.ValueKind!=JsonValueKind.Object)continue;
     var topic=block.TryGetProperty("topic",out var t)&&t.ValueKind==JsonValueKind.String?t.GetString():null;
     if(result.Count==0&&topic=="continue")continues=true;
     if(topic=="new"&&block.TryGetProperty("title",out var title)&&title.ValueKind==JsonValueKind.String&&Clean(title.GetString()).Length>0)result.Add(Clean(title.GetString()));
     if(block.TryGetProperty("paragraphs",out var p)&&p.ValueKind==JsonValueKind.Array)Strings(p,result);
    }
   return new(result.ToArray(),continues&&result.Count>0);
  }catch(JsonException){return new([],false);}
 }
 private static void Strings(JsonElement array,List<string> into){foreach(var x in array.EnumerateArray())if(x.ValueKind==JsonValueKind.String&&Clean(x.GetString()) is {Length:>0} s)into.Add(s);}
 public static string PlainText(IEnumerable<string> paragraphs)=>string.Join("\n\n",paragraphs);

 /// <summary>Reference context: the document's current last paragraph. Long paragraphs keep their opening (topic sentence) and ending.</summary>
 public static string Excerpt(string paragraph) {
  if(paragraph.Length<=MaxContextChars)return paragraph;
  const int head=200;
  return paragraph[..head]+"……"+paragraph[^(MaxContextChars-head)..];
 }
}
