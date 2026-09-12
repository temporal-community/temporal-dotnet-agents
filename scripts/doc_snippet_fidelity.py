#!/usr/bin/env python3
"""Compare harness snippet bodies against the doc blocks they quote.

Driven by scripts/verify-doc-snippet-fidelity.sh; see that file for the contract. Kept as a
separate .py rather than a heredoc so the self-test can exercise it directly and so editors
lint it.
"""
import os
import re
import sys
import glob


def slug(line):
    """Heading -> anchor. Must stay identical to the slugger in verify-doc-snippet-coverage.sh."""
    line = re.sub(r"^#+\s+", "", line)
    line = re.sub(r"\s+#+\s*$", "", line)
    line = line.replace("`", "")
    line = re.sub(r"\*\*|__|\*", "", line)
    line = line.lower()
    line = re.sub(r"[^a-z0-9 _-]", "", line)
    return line.replace(" ", "-")


def csharp_blocks_by_heading(doc_text):
    """{heading-slug: [block, ...]} plus {heading-slug: [section line, ...]}."""
    blocks, sections = {}, {}
    heading, in_fence, lang, body = "", False, "", []
    for line in doc_text.split("\n"):
        fence = re.match(r"^\s*(```|~~~)(.*)$", line)
        if fence:
            if not in_fence:
                in_fence, lang, body = True, fence.group(2).strip(), []
            else:
                in_fence = False
                if lang.startswith("csharp"):
                    blocks.setdefault(heading, []).append(body)
            sections.setdefault(heading, []).append(line)
            continue
        if in_fence:
            body.append(line)
            sections.setdefault(heading, []).append(line)
            continue
        if re.match(r"^#{1,6}\s", line):
            heading = slug(line)
        sections.setdefault(heading, []).append(line)
    return blocks, sections


# A leading `using X;`, optionally with a trailing comment. Only leading ones are dropped.
_USING = re.compile(r"^\s*using\s+[\w.]+\s*;\s*(//.*)?$")
# Preprocessor directives are excluded from the indent calculation: a `#pragma` written at column 0
# inside an indented block would otherwise make the common indent 0 and leave every other line
# still indented, so nothing would ever match.
_PREPROC = re.compile(r"^\s*#\s*(pragma|if|else|elif|endif|region|endregion|nullable|warning)\b")


def normalise(lines):
    """Dedent, drop leading usings/blanks, drop trailing blanks."""
    out = [ln.rstrip() for ln in lines]
    while out and (not out[0].strip() or _USING.match(out[0])):
        out.pop(0)
    while out and not out[-1].strip():
        out.pop()
    indents = [len(ln) - len(ln.lstrip())
               for ln in out if ln.strip() and not _PREPROC.match(ln)]
    pad = min(indents) if indents else 0
    result = []
    for ln in out:
        if not ln.strip():
            result.append("")
        elif _PREPROC.match(ln):
            result.append(ln.strip())
        else:
            result.append(ln[pad:])
    return result


def main():
    harness_dir, allowlist_path = sys.argv[1], sys.argv[2]

    adapted = {}
    if os.path.exists(allowlist_path):
        for raw in open(allowlist_path):
            line = raw.rstrip("\n")
            if not line.strip() or line.lstrip().startswith("#"):
                continue
            if "\t" not in line:
                print(f"ERROR: malformed allowlist line (needs a TAB): {line}", file=sys.stderr)
                return 2
            key, reason = line.split("\t", 1)
            adapted[key.strip()] = reason.strip()

    used, problems, exact, loose = set(), [], 0, 0
    claimed = {}

    for path in sorted(glob.glob(os.path.join(harness_dir, "**", "*.cs"), recursive=True)):
        lines = open(path).read().split("\n")
        i = 0
        while i < len(lines):
            m = re.search(r"BEGIN SNIPPET(-PROSE)?\s+(\S+)", lines[i])
            if not m:
                i += 1
                continue
            is_prose, key = bool(m.group(1)), m.group(2)
            doc_path, _, frag = key.partition("#")
            j, body = i + 1, []
            while j < len(lines) and "END SNIPPET" not in lines[j]:
                body.append(lines[j])
                j += 1
            if j >= len(lines):
                problems.append(f"{path}: BEGIN marker for {key} has no END marker")
                break
            if not os.path.exists(doc_path):
                problems.append(f"{path}: {key} names a doc that does not exist: {doc_path}")
                i = j + 1
                continue

            doc_text = open(doc_path).read()
            blocks, sections = csharp_blocks_by_heading(doc_text)
            if frag not in sections:
                problems.append(
                    f"{path}\n       key : {key}\n"
                    f"       No heading in {doc_path} has that anchor. It was renamed or removed."
                )
                i = j + 1
                continue

            snippet = normalise(body)
            section_lines = {ln.strip() for ln in sections[frag]}

            if key in adapted or is_prose:
                # An adapted snippet is exempt from line comparison — that is what "cannot be
                # verbatim" means. What is still enforced: the heading exists (checked above), the
                # exemption carries a written reason, and the exemption is still necessary (below).
                if is_prose and key not in adapted:
                    missing = [ln for ln in snippet
                               if ln.strip() and ln.strip() not in section_lines]
                    if missing:
                        problems.append(
                            f"{path}\n       key : {key}\n"
                            f"       prose snippet, but these lines are not in that section:\n"
                            + "".join(f"         {ln.strip()}\n" for ln in missing[:6])
                        )
                if key in adapted:
                    used.add(key)
                    # If an adapted snippet has become an exact match, the entry is dead weight.
                    for idx, block in enumerate(blocks.get(frag, [])):
                        if normalise(block) == snippet and (key, idx) not in claimed:
                            problems.append(
                                f"stale allowlist entry — {key} now matches its doc block exactly.\n"
                                f"       Remove it from {allowlist_path}."
                            )
                            break
                loose += 1
                i = j + 1
                continue

            candidates = blocks.get(frag, [])
            if not candidates:
                problems.append(
                    f"{path}\n       key : {key}\n"
                    f"       That heading has no ```csharp block. Use BEGIN SNIPPET-PROSE for a\n"
                    f"       prose claim, or point the marker at the heading that holds the block."
                )
                i = j + 1
                continue

            hit = None
            for idx, block in enumerate(candidates):
                if (key, idx) in claimed:
                    continue
                if normalise(block) == snippet:
                    hit = idx
                    break
            if hit is None:
                best = normalise(candidates[0])
                only_doc = [ln for ln in best if ln not in snippet and ln.strip()]
                only_harness = [ln for ln in snippet if ln not in best and ln.strip()]
                detail = ""
                if only_doc:
                    detail += "       in the doc but NOT compiled:\n" + "".join(
                        f"         {ln.strip()}\n" for ln in only_doc[:6])
                if only_harness:
                    detail += "       in the harness but NOT in the doc:\n" + "".join(
                        f"         {ln.strip()}\n" for ln in only_harness[:6])
                problems.append(
                    f"{path}\n       key : {key}\n"
                    f"       Snippet does not match the ```csharp block under that heading.\n"
                    + detail
                    + f"       Re-sync it, or add {key} to {allowlist_path} with a reason."
                )
            else:
                claimed[(key, hit)] = path
                exact += 1
            i = j + 1

    for key in sorted(set(adapted) - used):
        problems.append(
            f"stale allowlist entry — no snippet uses key {key}.\n"
            f"       Remove it from {allowlist_path}."
        )

    for p in problems:
        print(f"ERROR: {p}", file=sys.stderr)
    if problems:
        print(f"\nDoc-snippet fidelity: {len(problems)} problem(s).", file=sys.stderr)
        return 1

    print(f"Doc-snippet fidelity OK: {exact} snippet(s) match their doc block exactly, "
          f"{loose} adapted snippet(s) contained in their section.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
