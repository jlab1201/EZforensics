<!--
Reminders and fallback texts the app sends to the model or shows the examiner when the model misbehaves.
One "## key" heading per text. Placeholders: {tool} {tools} {max} {category}. Keep the keys; edit the text under them.
-->

## action_nudge
I asked for an action. Call the matching tool now (grid_flag_rows, grid_unflag_rows, grid_apply_filter, grid_sort, grid_clear_filter, grid_go_to_row, analyze_open_file, ingest_select_source, ingest_start, explore_search, explore_open or app_go_to_page), using what we just talked about; find rows with csv_query first if needed. Do not explain the clicks: do it. Then reply in one short line. Do not list the rows.

## action_nudge_tool
I asked for an action. Call {tool} now, using what we just talked about; find rows with csv_query first if needed. Do not say it is done without calling it: nothing changes in the app until you call the tool. Then reply in one short line.

## action_nudge_offered
I asked for an action. Call the matching tool now ({tools}), using what we just talked about. Do not say it is done without calling it: nothing changes in the app until you call the tool. Then reply in one short line.

## nothing_applied
Nothing was changed: Spectre answered without using a tool. Ask again in a few words, e.g. "filter Name contains powershell" or "go to Explore".

## empty_reply
Your reply was empty. Reply to my previous message now: if I asked to filter, flag, unflag, sort, clear the filter, go to a row or open a file, call that tool; otherwise answer briefly.

## empty_after_tools
Your reply was empty. Reply now in one or two lines with what you found or did.

## empty_after_action
Done. See the card above.

## empty_after_read
Spectre looked at the case but gave no answer. Ask again, more narrowly.

## empty_no_tool
Spectre gave an empty reply (the local model sometimes does). Ask again or rephrase.

## max_rounds
Spectre stopped after {max} tool rounds. Ask a narrower question.

## cut_off
The answer was cut off (length limit). Ask a narrower question.

## refusal
The model declined this request (category: {category}). Rephrase, or continue without Spectre.
