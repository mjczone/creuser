/**
 * Strip the `allowed_commands:` block (a list of `- name` entries OR a
 * flow-style `[git, jq]` array) from raw frontmatter and return the
 * cleaned YAML + the extracted commands. Used by the Jobs editor to lift
 * that field out of the textarea so the chip-picker can own it.
 *
 * The split is line-based on YAML's two-space-indented sequence convention
 * — what our serializer emits and what most authors write. Operators who
 * hand-author flow-style lists get the same outcome via the regex branch.
 */
export function splitAllowedCommands(rawYaml: string): {
  yaml: string;
  commands: string[];
} {
  const lines = rawYaml.replace(/\r\n?/g, '\n').split('\n');
  const out: string[] = [];
  const cmds: string[] = [];
  let inBlock = false;
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i] ?? '';
    if (!inBlock) {
      // Flow-style on the same line: `allowed_commands: [git, jq]`
      const flow = /^allowed_commands\s*:\s*\[(.*)\]\s*$/.exec(line);
      if (flow) {
        for (const item of flow[1]!.split(',')) {
          const t = item.trim().replace(/^['"]|['"]$/g, '');
          if (t) cmds.push(t);
        }
        continue;
      }
      // Block-style header: `allowed_commands:` followed by `  - name` lines.
      if (/^allowed_commands\s*:\s*$/.test(line)) {
        inBlock = true;
        continue;
      }
      out.push(line);
    } else {
      const itemMatch = /^\s+-\s+['"]?([^'"\s]+)['"]?\s*$/.exec(line);
      if (itemMatch) {
        cmds.push(itemMatch[1]!);
        continue;
      }
      // Indented continuation we don't recognize — be conservative, end the block.
      inBlock = false;
      out.push(line);
    }
  }
  return { yaml: out.join('\n'), commands: cmds };
}

/**
 * Append the `allowed_commands:` block to user-edited YAML. Always writes
 * block style for readability; round-trips with <see cref="splitAllowedCommands"/>.
 */
export function injectAllowedCommands(rawYaml: string, commands: string[]): string {
  let yaml = rawYaml.trimEnd();
  if (commands.length > 0) {
    if (yaml.length > 0) yaml += '\n';
    yaml += 'allowed_commands:\n';
    for (const c of commands) yaml += `  - ${c}\n`;
  } else if (yaml.length > 0) {
    yaml += '\n';
  }
  return yaml;
}
