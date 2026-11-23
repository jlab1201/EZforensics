These files are the instructions Spectre (the assistant) sends to the model.
- system.md: the system prompt, sent as the first message of every chat.
- nudges.md: reminders sent when the model skips a tool or answers empty, and the fallback texts shown to you.
- text-tools.md: how tool calls are explained to models without native tool support.
Load order: a file in <data folder>\prompts\ wins; otherwise the copy built into the app (also shipped next to the exe in prompts\).
To override, copy a file into <data folder>\prompts\ (Options > AI > Open prompt files) and edit it. Delete it to go back.
Files with "## key" headings must keep their keys. The leading comment block is not sent. Restart the app after editing.
