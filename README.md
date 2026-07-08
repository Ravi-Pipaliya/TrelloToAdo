# TrelloToAdo

A standalone .NET 8 console app that migrates cards from a Trello board into Azure DevOps work items. No external NuGet packages required — just the base class library (`System.Net.Http`, `System.Text.Json`).

## What gets migrated

- Card title → work item title (Azure DevOps type is configurable, defaults to **User Story**)
- Card description → work item Description, converted from Trello's Markdown to HTML (bold, italic, links, images, bullet lists)
- A `**Acceptance Criteria:**` section in the description → split out into Azure DevOps's dedicated Acceptance Criteria field
- Trello list name and labels → tags
- Checklist items → child Tasks under the work item
- Comments → work item comments, prefixed with the original author + date (Azure DevOps has no API-level way to post "as" another user)
- Attachments hosted on Trello → downloaded and re-uploaded as native Azure DevOps attachments, including images embedded inline in the description text
- External attachment links (Google Drive, etc.) → added as a comment instead of downloaded
- Duplicate images (the same picture uploaded as both a formal attachment and pasted inline in the description) are deduplicated where possible, so they aren't uploaded twice

## Setup

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download).
2. Copy `setup-env.ps1.template` to `setup-env.ps1` and fill in your real values (see comments in the file for where to find each one). `setup-env.ps1` is gitignored — never commit it.
3. Restore & build:
   ```
   dotnet build
   ```

## Usage

Dry run (default — prints what *would* happen, makes no changes):
```
dotnet run
```

Small real test (creates only the first 5 cards for real):
```
dotnet run -- --apply --limit 5
```

Full migration:
```
dotnet run -- --apply
```

Other flags:
- `--skip-attachments` — don't download/upload attachments
- `--skip-comments` — don't migrate comments

## Notes

- There's no dedup/resume logic across runs — running the same command twice creates duplicate work items. If a run gets interrupted partway through, check Azure DevOps before re-running.
- The target Azure DevOps project's process template must have whatever work item type you configure (`ADO_WORK_ITEM_TYPE`, default `User Story`). Projects on the **Basic** process template don't have "User Story" — use `"Issue"` instead, or switch the project to the Agile process template.
