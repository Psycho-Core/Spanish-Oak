#!/usr/bin/env python3
"""Parse every C# file in the tree with tree-sitter and report syntax errors.

There is no .NET SDK in every environment this project is worked on, so this is the fastest
way to catch a broken brace before pushing something that CI has to reject.

    python3 -m venv .venv && .venv/bin/pip install tree_sitter tree_sitter_c_sharp
    .venv/bin/python tools/csharp_parse.py [paths...]

Exit code 0 when every file parses, 1 otherwise.
"""
import sys
import pathlib

try:
    from tree_sitter import Language, Parser
    import tree_sitter_c_sharp as tscs
except ImportError:  # pragma: no cover - developer convenience only
    print("tree_sitter / tree_sitter_c_sharp are not installed; skipping parse check.")
    sys.exit(0)


def make_parser():
    language = Language(tscs.language())
    parser = Parser()
    try:
        parser.language = language
    except AttributeError:  # older tree_sitter API
        parser.set_language(language)
    return parser


def walk_errors(node, found):
    if node.type == "ERROR" or node.is_missing:
        row, col = node.start_point
        found.append((row + 1, col + 1, node.type))
    for child in node.children:
        walk_errors(child, found)


def main(argv):
    roots = argv[1:] or ["src", "tests"]
    files = []
    for root in roots:
        path = pathlib.Path(root)
        if path.is_dir():
            files.extend(sorted(path.rglob("*.cs")))
        elif path.suffix == ".cs":
            files.append(path)
    if not files:
        print("no C# files found")
        return 1

    parser = make_parser()
    total_issues = 0
    for path in files:
        tree = parser.parse(path.read_bytes())
        issues = []
        walk_errors(tree.root_node, issues)
        total_issues += len(issues)
        status = "OK " if not issues else "BAD"
        print("%s %s  (%d issue%s)" % (status, path, len(issues), "" if len(issues) == 1 else "s"))
        for line, col, kind in issues[:5]:
            print("      line %d:%d  %s" % (line, col, kind))

    print("files: %d issues: %d" % (len(files), total_issues))
    return 0 if total_issues == 0 else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
