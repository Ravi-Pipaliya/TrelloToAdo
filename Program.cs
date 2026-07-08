// TrelloToAdo - migrates Trello cards into Azure DevOps User Stories.
//
// Standalone .NET 8 console app. No external NuGet packages required -
// uses only the base class library (System.Net.Http, System.Text.Json).
//
// ---------------------------------------------------------------------
// SETUP
// ---------------------------------------------------------------------
// 1. Install the .NET 8 SDK if you don't have it: https://dotnet.microsoft.com/download
//
// 2. Set these environment variables (do NOT hardcode credentials in code):
//
//    TRELLO_KEY        Your Trello API key
//    TRELLO_TOKEN      Your Trello API token
//    TRELLO_BOARD_ID   The Trello board ID (from the board URL, or via
//                       https://trello.com/1/members/me/boards?key=...&token=...)
//    ADO_ORG           Azure DevOps org name, e.g. "contoso"
//    ADO_PROJECT       Azure DevOps project name, e.g. "MyProject"
//    ADO_PAT           Azure DevOps Personal Access Token (Work Items: Read & Write)
//
//    Windows (PowerShell):
//      $env:TRELLO_KEY="..."; $env:TRELLO_TOKEN="..."; $env:TRELLO_BOARD_ID="..."
//      $env:ADO_ORG="contoso"; $env:ADO_PROJECT="MyProject"; $env:ADO_PAT="..."
//
//    macOS/Linux:
//      export TRELLO_KEY=... TRELLO_TOKEN=... TRELLO_BOARD_ID=...
//      export ADO_ORG=contoso ADO_PROJECT=MyProject ADO_PAT=...
//
// 3. Restore & build:
//      dotnet build
//
// 4. Dry run (default - prints what WOULD happen, makes no changes):
//      dotnet run
//
// 5. Small sample (creates only the first 5 cards for real):
//      dotnet run -- --apply --limit 5
//
// 6. Full migration:
//      dotnet run -- --apply
//
// ---------------------------------------------------------------------
// WHAT GETS MIGRATED
// ---------------------------------------------------------------------
// - Card title/description  -> User Story title/description
// - Trello list name        -> Tag ("Trello-List:<name>")
// - Labels                  -> Tags ("Trello-Label:<name>")
// - Checklist items         -> Child Tasks under the User Story
// - Comments                -> Work item comments, prefixed with the
//                              original author + date (Azure DevOps has
//                              no API-level way to post "as" another user)
// - Attachments hosted on Trello -> downloaded and re-uploaded as native
//                              Azure DevOps attachments
// - External attachment links (Google Drive etc.) -> added as a comment
//                              instead of downloaded
// ---------------------------------------------------------------------

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var options = CliOptions.Parse(args);

string Require(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value))
    {
        Console.WriteLine($"Missing required environment variable: {name}");
        Environment.Exit(1);
    }
    return value!;
}

var trelloKey = Require("TRELLO_KEY");
var trelloToken = Require("TRELLO_TOKEN");
var trelloBoardId = Require("TRELLO_BOARD_ID");
var adoOrg = Require("ADO_ORG");
var adoProject = Require("ADO_PROJECT");
var adoPat = Require("ADO_PAT");
var adoWorkItemType = Environment.GetEnvironmentVariable("ADO_WORK_ITEM_TYPE");
if (string.IsNullOrWhiteSpace(adoWorkItemType)) adoWorkItemType = "User Story";

var trello = new TrelloClient(trelloKey, trelloToken);
var ado = new AzureDevOpsClient(adoOrg, adoProject, adoPat);

Console.WriteLine($"Fetching board data from Trello board {trelloBoardId} ...");
var lists = await trello.GetListsAsync(trelloBoardId);
var cards = await trello.GetCardsAsync(trelloBoardId);

var toProcess = options.Limit.HasValue ? cards.Take(options.Limit.Value).ToList() : cards;
Console.WriteLine($"Found {cards.Count} cards. Migrating {(options.Limit.HasValue ? options.Limit.ToString() : "ALL")} of them.");
Console.WriteLine(options.DryRun ? "Mode: DRY RUN (no changes will be made)\n" : "Mode: APPLY (changes will be made)\n");

for (int i = 0; i < toProcess.Count; i++)
{
    var card = toProcess[i];
    var title = card.GetProperty("name").GetString() ?? "(untitled)";
    var desc = card.TryGetProperty("desc", out var d) ? d.GetString() ?? "" : "";
    var idList = card.GetProperty("idList").GetString() ?? "";
    var listName = lists.TryGetValue(idList, out var ln) ? ln : "Unknown List";

    var tags = new List<string> { listName };
    if (card.TryGetProperty("labels", out var labelsEl))
    {
        foreach (var label in labelsEl.EnumerateArray())
        {
            var name = label.TryGetProperty("name", out var ln2) ? ln2.GetString() : null;
            if (!string.IsNullOrWhiteSpace(name))
                tags.Add(name);
        }
    }

    var (descBody, acceptanceCriteria) = MarkdownConverter.SplitAcceptanceCriteria(desc);
    var descHtml = MarkdownConverter.ToHtml(descBody);
    var acceptanceCriteriaHtml = MarkdownConverter.ToHtml(acceptanceCriteria);

    Console.WriteLine($"[{i + 1}/{toProcess.Count}] {title}");

    int workItemId = 0;
    if (options.DryRun)
    {
        Console.WriteLine($"  [dry-run] Would create {adoWorkItemType}: \"{title}\" tags=[{string.Join(", ", tags)}]");
    }
    else
    {
        workItemId = await ado.CreateWorkItemAsync(adoWorkItemType, title, descHtml, acceptanceCriteriaHtml, tags);
    }

    // Checklists -> child tasks
    if (card.TryGetProperty("checklists", out var checklistsEl))
    {
        foreach (var checklist in checklistsEl.EnumerateArray())
        {
            if (!checklist.TryGetProperty("checkItems", out var itemsEl)) continue;
            foreach (var item in itemsEl.EnumerateArray())
            {
                var itemName = item.GetProperty("name").GetString() ?? "(untitled item)";
                if (options.DryRun)
                {
                    Console.WriteLine($"    [dry-run] Would create child Task: \"{itemName}\" under #{workItemId}");
                }
                else
                {
                    await ado.CreateChildTaskAsync(workItemId, itemName);
                }
            }
        }
    }

    // Comments
    if (!options.SkipComments)
    {
        var cardId = card.GetProperty("id").GetString()!;
        var comments = await trello.GetCommentsAsync(cardId);
        foreach (var action in comments)
        {
            var author = action.GetProperty("memberCreator").GetProperty("fullName").GetString() ?? "Unknown";
            var date = action.GetProperty("date").GetString() ?? "";
            var dateOnly = date.Length >= 10 ? date[..10] : date;
            var text = action.GetProperty("data").GetProperty("text").GetString() ?? "";
            var formatted = $"[Originally by {author}, {dateOnly}]: {text}";

            if (options.DryRun)
                Console.WriteLine($"    [dry-run] Would add comment to #{workItemId}: \"{Truncate(formatted, 60)}\"");
            else
                await ado.AddCommentAsync(workItemId, formatted);
        }
    }

    // Tracks bytes already uploaded for this card (by content hash), and
    // every URL Trello itself considers a rendition of an already-uploaded
    // attachment (its own url plus its preview urls) - so the same picture
    // referenced twice, once as a formal attachment and once embedded
    // inline (often as a different format/size rendition), is only
    // uploaded to Azure DevOps once.
    var uploadedByHash = new Dictionary<string, string>();
    var uploadedByKnownUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    // Trello sometimes has the same picture as two independent attachments
    // (e.g. one formally attached, one pasted inline as a different
    // format/rendition) with no data-model link between them. Matching by
    // filename with the extension stripped catches this common case.
    var uploadedByBaseName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // Attachments listed on the card
    if (!options.SkipAttachments && card.TryGetProperty("attachments", out var attachmentsEl))
    {
        foreach (var att in attachmentsEl.EnumerateArray())
        {
            var name = att.GetProperty("name").GetString() ?? "attachment";
            var url = att.GetProperty("url").GetString() ?? "";

            if (!url.StartsWith("https://trello.com", StringComparison.OrdinalIgnoreCase))
            {
                // External link (Google Drive etc.) - note as a comment instead of downloading
                if (options.DryRun)
                    Console.WriteLine($"    [dry-run] Would add note for external attachment: {name} - {url}");
                else
                    await ado.AddCommentAsync(workItemId, $"Linked attachment (external): {name} - {url}");
                continue;
            }

            if (options.DryRun)
            {
                Console.WriteLine($"    [dry-run] Would download + upload attachment: {name}");
                continue;
            }

            byte[]? bytes = null;
            try
            {
                bytes = await trello.DownloadAttachmentAsync(url);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Could not download attachment '{name}' from Trello ({ex.Message}); adding as a link instead.");
                await ado.AddCommentAsync(workItemId, $"Linked attachment (could not be downloaded automatically): {name} - {url}");
                continue;
            }

            try
            {
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                string adoAttachmentUrl;
                if (uploadedByHash.TryGetValue(hash, out var existingUrl))
                {
                    adoAttachmentUrl = existingUrl;
                    await ado.LinkAttachmentAsync(workItemId, existingUrl);
                }
                else
                {
                    uploadedByHash[hash] = adoAttachmentUrl = await ado.UploadAttachmentAsync(workItemId, name, bytes);
                }

                uploadedByKnownUrl[url] = adoAttachmentUrl;
                if (att.TryGetProperty("previews", out var previewsEl))
                    foreach (var preview in previewsEl.EnumerateArray())
                        if (preview.TryGetProperty("url", out var previewUrlEl) && previewUrlEl.GetString() is { } previewUrl)
                            uploadedByKnownUrl[previewUrl] = adoAttachmentUrl;
                uploadedByBaseName[Path.GetFileNameWithoutExtension(name)] = adoAttachmentUrl;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Failed to upload attachment '{name}' to Azure DevOps: {ex.Message}");
            }
        }
    }

    // Images embedded directly in the description text (Trello preview
    // links; SplitAcceptanceCriteria always moves these into descBody).
    // These don't always correspond to an entry in the card's own
    // attachments list - sometimes they reference an attachment from
    // elsewhere entirely - so fetch each one by its own URL rather than
    // trying to match it against the attachments list.
    var descriptionNeedsUpdate = false;
    if (!options.SkipAttachments && !options.DryRun)
    {
        var embeddedUrls = MarkdownConverter.ExtractTrelloImageUrls(descBody).Distinct();

        foreach (var embeddedUrl in embeddedUrls)
        {
            var fileName = embeddedUrl.Split('/').Last();

            if (uploadedByKnownUrl.TryGetValue(embeddedUrl, out var knownAdoUrl) ||
                uploadedByBaseName.TryGetValue(Path.GetFileNameWithoutExtension(fileName), out knownAdoUrl))
            {
                descHtml = descHtml.Replace(embeddedUrl, knownAdoUrl);
                acceptanceCriteriaHtml = acceptanceCriteriaHtml.Replace(embeddedUrl, knownAdoUrl);
                descriptionNeedsUpdate = true;
                continue;
            }

            try
            {
                var bytes = await trello.DownloadAttachmentAsync(embeddedUrl);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                string adoAttachmentUrl;
                if (uploadedByHash.TryGetValue(hash, out var existingUrl))
                    adoAttachmentUrl = existingUrl;
                else
                    uploadedByHash[hash] = adoAttachmentUrl = await ado.UploadAttachmentAsync(workItemId, fileName, bytes);
                descHtml = descHtml.Replace(embeddedUrl, adoAttachmentUrl);
                acceptanceCriteriaHtml = acceptanceCriteriaHtml.Replace(embeddedUrl, adoAttachmentUrl);
                descriptionNeedsUpdate = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Could not retrieve embedded image '{fileName}' from Trello ({ex.Message}); leaving original link in place.");
            }
        }
    }

    if (descriptionNeedsUpdate)
        await ado.UpdateDescriptionFieldsAsync(workItemId, descHtml, acceptanceCriteriaHtml);

    await Task.Delay(300); // basic rate-limit courtesy
}

Console.WriteLine("Done.");

static string Truncate(string s, int len) => s.Length <= len ? s : s[..len] + "...";

// =======================================================================

class CliOptions
{
    public bool DryRun { get; set; } = true;
    public int? Limit { get; set; }
    public bool SkipAttachments { get; set; }
    public bool SkipComments { get; set; }

    public static CliOptions Parse(string[] args)
    {
        var opts = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--apply":
                    opts.DryRun = false;
                    break;
                case "--limit":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var lim))
                    {
                        opts.Limit = lim;
                        i++;
                    }
                    break;
                case "--skip-attachments":
                    opts.SkipAttachments = true;
                    break;
                case "--skip-comments":
                    opts.SkipComments = true;
                    break;
            }
        }
        return opts;
    }
}

class TrelloClient
{
    private readonly HttpClient _http = new();
    private readonly string _key;
    private readonly string _token;
    private const string BaseUrl = "https://api.trello.com/1";

    public TrelloClient(string key, string token)
    {
        _key = key;
        _token = token;
    }

    private string Auth(string sep = "?") => $"{sep}key={_key}&token={_token}";

    public async Task<Dictionary<string, string>> GetListsAsync(string boardId)
    {
        var json = await _http.GetStringAsync($"{BaseUrl}/boards/{boardId}/lists{Auth()}");
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, string>();
        foreach (var lst in doc.RootElement.EnumerateArray())
            result[lst.GetProperty("id").GetString()!] = lst.GetProperty("name").GetString() ?? "";
        return result;
    }

    public async Task<List<JsonElement>> GetCardsAsync(string boardId)
    {
        var url = $"{BaseUrl}/boards/{boardId}/cards{Auth()}&attachments=true&attachment_fields=all&checklists=all&fields=name,desc,idList,labels,shortUrl,id";
        var json = await _http.GetStringAsync(url);
        // Keep the JsonDocument alive via cloned elements so it can be enumerated after disposal.
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    public async Task<List<JsonElement>> GetCommentsAsync(string cardId)
    {
        var url = $"{BaseUrl}/cards/{cardId}/actions{Auth()}&filter=commentCard";
        var json = await _http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    public async Task<byte[]> DownloadAttachmentAsync(string url)
    {
        // Card attachment URLs come back as https://trello.com/... (the
        // browser-facing link). The programmatic download endpoint lives
        // on api.trello.com at the same path, and unlike other Trello API
        // endpoints, it rejects key/token as query params - it requires
        // them as an OAuth Authorization header instead.
        var apiUrl = url.Replace("https://trello.com/", "https://api.trello.com/", StringComparison.OrdinalIgnoreCase);
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", $"oauth_consumer_key=\"{_key}\", oauth_token=\"{_token}\"");
        try
        {
            var resp = await _http.SendAsync(request);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex)
        {
            throw new Exception($"{ex.Message} (fetched: {apiUrl})", ex);
        }
    }
}

class AzureDevOpsClient
{
    private readonly HttpClient _http = new();
    private readonly string _org;
    private readonly string _project;

    public AzureDevOpsClient(string org, string project, string pat)
    {
        _org = org;
        _project = project;
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    private string ApiBase => $"https://dev.azure.com/{_org}/{Uri.EscapeDataString(_project)}/_apis";

    public async Task<int> CreateWorkItemAsync(string workItemType, string title, string description, string acceptanceCriteria, List<string> tags)
    {
        var patch = new List<object>
        {
            new { op = "add", path = "/fields/System.Title", value = title },
            new { op = "add", path = "/fields/System.Description", value = description ?? "" },
        };
        if (!string.IsNullOrWhiteSpace(acceptanceCriteria))
            patch.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Common.AcceptanceCriteria", value = acceptanceCriteria });
        if (tags.Count > 0)
            patch.Add(new { op = "add", path = "/fields/System.Tags", value = string.Join("; ", tags) });

        var url = $"{ApiBase}/wit/workitems/${Uri.EscapeDataString(workItemType)}?api-version=7.1-preview.3";
        using var content = new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json-patch+json");
        var resp = await _http.PatchAsync(url, content);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetInt32();
    }

    public async Task<int> CreateChildTaskAsync(int parentId, string title)
    {
        var patch = new List<object>
        {
            new { op = "add", path = "/fields/System.Title", value = title },
        };
        var url = $"{ApiBase}/wit/workitems/$Task?api-version=7.1-preview.3";
        using var content = new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json-patch+json");
        var resp = await _http.PatchAsync(url, content);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var taskId = doc.RootElement.GetProperty("id").GetInt32();

        var linkPatch = new List<object>
        {
            new
            {
                op = "add",
                path = "/relations/-",
                value = new
                {
                    rel = "System.LinkTypes.Hierarchy-Reverse",
                    url = $"{ApiBase}/wit/workItems/{parentId}"
                }
            }
        };
        using var linkContent = new StringContent(JsonSerializer.Serialize(linkPatch), Encoding.UTF8, "application/json-patch+json");
        var linkResp = await _http.PatchAsync($"{ApiBase}/wit/workitems/{taskId}?api-version=7.1-preview.3", linkContent);
        linkResp.EnsureSuccessStatusCode();
        return taskId;
    }

    public async Task AddCommentAsync(int workItemId, string text)
    {
        var url = $"{ApiBase}/wit/workItems/{workItemId}/comments?api-version=7.1-preview.3";
        using var content = new StringContent(JsonSerializer.Serialize(new { text }), Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync(url, content);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<string> UploadAttachmentBytesAsync(string fileName, byte[] bytes)
    {
        var uploadUrl = $"{ApiBase}/wit/attachments?fileName={Uri.EscapeDataString(fileName)}&api-version=7.1-preview.3";
        using var byteContent = new ByteArrayContent(bytes);
        byteContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var uploadResp = await _http.PostAsync(uploadUrl, byteContent);
        uploadResp.EnsureSuccessStatusCode();
        var uploadBody = await uploadResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(uploadBody);
        return doc.RootElement.GetProperty("url").GetString()!;
    }

    public async Task LinkAttachmentAsync(int workItemId, string attachmentUrl)
    {
        var linkPatch = new List<object>
        {
            new
            {
                op = "add",
                path = "/relations/-",
                value = new
                {
                    rel = "AttachedFile",
                    url = attachmentUrl,
                    attributes = new { comment = "Migrated from Trello" }
                }
            }
        };
        using var linkContent = new StringContent(JsonSerializer.Serialize(linkPatch), Encoding.UTF8, "application/json-patch+json");
        var linkResp = await _http.PatchAsync($"{ApiBase}/wit/workitems/{workItemId}?api-version=7.1-preview.3", linkContent);
        linkResp.EnsureSuccessStatusCode();
    }

    public async Task<string> UploadAttachmentAsync(int workItemId, string fileName, byte[] bytes)
    {
        var attachmentUrl = await UploadAttachmentBytesAsync(fileName, bytes);
        await LinkAttachmentAsync(workItemId, attachmentUrl);
        return attachmentUrl;
    }

    public async Task UpdateDescriptionFieldsAsync(int workItemId, string description, string acceptanceCriteria)
    {
        var patch = new List<object>
        {
            new { op = "replace", path = "/fields/System.Description", value = description ?? "" },
        };
        if (!string.IsNullOrWhiteSpace(acceptanceCriteria))
            patch.Add(new { op = "replace", path = "/fields/Microsoft.VSTS.Common.AcceptanceCriteria", value = acceptanceCriteria });

        using var content = new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json-patch+json");
        var resp = await _http.PatchAsync($"{ApiBase}/wit/workitems/{workItemId}?api-version=7.1-preview.3", content);
        resp.EnsureSuccessStatusCode();
    }
}

static class MarkdownConverter
{
    private static readonly Regex AcceptanceCriteriaHeading =
        new(@"\*\*\s*Acceptance Criteria:?\s*\*\*", RegexOptions.IgnoreCase);
    private static readonly Regex ImageMarkdown =
        new(@"!\[[^\]]*\]\([^)]+\)", RegexOptions.IgnoreCase);

    // Trello descriptions often embed "**Acceptance Criteria:**" as a
    // section heading rather than a separate field. Azure DevOps has a
    // dedicated Acceptance Criteria field, so split it out. Embedded images
    // are card-level attachments (screenshots, mockups) rather than
    // acceptance criteria content, so they're pulled out first and always
    // placed in the Description regardless of where they appeared in the
    // original text.
    public static (string Description, string AcceptanceCriteria) SplitAcceptanceCriteria(string desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return (desc, "");

        var images = ImageMarkdown.Matches(desc).Select(m => m.Value).ToList();
        var textOnly = ImageMarkdown.Replace(desc, "").TrimEnd();

        var match = AcceptanceCriteriaHeading.Match(textOnly);
        string description, acceptanceCriteria;
        if (!match.Success)
        {
            description = textOnly;
            acceptanceCriteria = "";
        }
        else
        {
            description = textOnly[..match.Index].TrimEnd();
            acceptanceCriteria = textOnly[(match.Index + match.Length)..].TrimStart();
        }

        if (images.Count > 0)
            description = (description + "\n\n" + string.Join("\n", images)).Trim();

        return (description, acceptanceCriteria);
    }

    // Finds Trello-hosted image URLs embedded via Markdown image syntax
    // (![alt](url)) in raw card text, so they can be fetched and re-hosted
    // as native Azure DevOps attachments.
    public static List<string> ExtractTrelloImageUrls(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return new List<string>();
        return ImageMarkdown.Matches(markdown)
            .Select(m => Regex.Match(m.Value, @"\(([^)]+)\)").Groups[1].Value)
            .Where(url => url.StartsWith("https://trello.com", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // Converts the small subset of Markdown Trello actually uses in card
    // descriptions (bold, italic, images, "- " bullet lists, blank-line
    // paragraphs) into HTML, since Azure DevOps's Description and
    // Acceptance Criteria fields render HTML, not Markdown.
    public static string ToHtml(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";

        var text = markdown.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        text = Regex.Replace(text, @"!\[([^\]]*)\]\(([^)]+)\)",
            m => $"<img src=\"{m.Groups[2].Value}\" alt=\"{m.Groups[1].Value}\" style=\"max-width:100%;\" />");
        text = Regex.Replace(text, @"\[([^\]]+)\]\(([^)]+)\)", "<a href=\"$2\">$1</a>");
        text = Regex.Replace(text, @"\*\*([^*]+)\*\*", "<b>$1</b>");
        text = Regex.Replace(text, @"\*([^*]+)\*", "<i>$1</i>");

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        var inList = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- "))
            {
                if (!inList) { sb.Append("<ul>"); inList = true; }
                sb.Append($"<li>{trimmed[2..]}</li>");
            }
            else
            {
                if (inList) { sb.Append("</ul>"); inList = false; }
                if (trimmed.Length == 0) sb.Append("<br/>");
                else sb.Append($"<div>{line}</div>");
            }
        }
        if (inList) sb.Append("</ul>");

        return sb.ToString();
    }
}
