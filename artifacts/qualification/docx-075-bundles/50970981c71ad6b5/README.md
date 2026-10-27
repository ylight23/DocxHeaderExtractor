# One model call

You are standing in for the language model behind one API call. Answer it exactly as that model would:
read the conversation below in order and write the reply to the **last** message.

Use only the files in this folder. Do not open any other folder, repository file, earlier answer, score or reference
output. Do not use anything you remember from other tasks.

## The conversation (in order)
- `00-system.txt` (system, 7 lines)
- `01-user-packet.json` (user, 82 lines)
- `01-user-packet-long-lines.txt` (full text of 1 long line(s) of 01-user-packet.json, 4 lines)

`*-system.txt` is the system message. Read every text file completely (page through long files with offset/limit until
the end; a decision about an item must come from reading it). Look at every image.
The request offered optional tools; they are not available in this run, so answer directly without calling any.
The reply must be one JSON object (response_format {"type": "json_object"}), with no markdown fence
and no text around it.

## Submit
1. Write the JSON reply to `answer.json` in this folder.
2. Run: `python "C:\DocxHeaderExtractor-a99-closed-loop\tools\qualification\haiku_bridge\finalize.py" "C:\DocxHeaderExtractor-a99-closed-loop\artifacts\qualification\docx-075-bundles\50970981c71ad6b5"`
3. If it prints `ERROR`, fix `answer.json` and run it again. Stop when it prints `SUBMITTED`.
