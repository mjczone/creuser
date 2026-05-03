import { describe, expect, it } from 'vitest';
import {
  injectAllowedCommands,
  splitAllowedCommands,
} from 'src/composables/jobYamlHelpers';

describe('splitAllowedCommands', () => {
  it('returns empty commands when the YAML has none', () => {
    const result = splitAllowedCommands('type: shell\n');
    expect(result.yaml).toBe('type: shell\n');
    expect(result.commands).toEqual([]);
  });

  it('extracts a block-style allowed_commands list', () => {
    const yaml = 'type: shell\nallowed_commands:\n  - git\n  - rg\n  - fd\n';
    const result = splitAllowedCommands(yaml);
    expect(result.commands).toEqual(['git', 'rg', 'fd']);
    expect(result.yaml).toBe('type: shell\n');
  });

  it('extracts a flow-style allowed_commands list', () => {
    const yaml = 'type: shell\nallowed_commands: [git, rg, fd]\n';
    const result = splitAllowedCommands(yaml);
    expect(result.commands).toEqual(['git', 'rg', 'fd']);
    expect(result.yaml).toBe('type: shell\n');
  });

  it('handles flow-style with quoted items', () => {
    const yaml = 'allowed_commands: ["git", \'rg\']';
    const result = splitAllowedCommands(yaml);
    expect(result.commands).toEqual(['git', 'rg']);
  });

  it('preserves surrounding YAML when extracting the block', () => {
    const yaml = [
      'type: shell',
      'pattern: deterministic',
      'allowed_commands:',
      '  - git',
      '  - jq',
      'budgets:',
      '  max_duration_seconds: 600',
    ].join('\n');
    const result = splitAllowedCommands(yaml);
    expect(result.commands).toEqual(['git', 'jq']);
    expect(result.yaml).toBe(
      'type: shell\npattern: deterministic\nbudgets:\n  max_duration_seconds: 600',
    );
  });

  it('treats CRLF identically to LF', () => {
    const yaml = 'type: shell\r\nallowed_commands:\r\n  - git\r\n';
    const result = splitAllowedCommands(yaml);
    expect(result.commands).toEqual(['git']);
    expect(result.yaml).toBe('type: shell\n');
  });

  it('ends the block conservatively on unindented continuation', () => {
    // After the block header, the first non-`- name` line ends the block —
    // important so subsequent top-level keys aren't accidentally consumed.
    const yaml =
      'allowed_commands:\n  - git\nbudgets:\n  max_tokens: 50000\n';
    const result = splitAllowedCommands(yaml);
    expect(result.commands).toEqual(['git']);
    expect(result.yaml).toBe('budgets:\n  max_tokens: 50000\n');
  });
});

describe('injectAllowedCommands', () => {
  it('appends the block when commands are present', () => {
    const result = injectAllowedCommands('type: shell\n', ['git', 'jq']);
    expect(result).toBe('type: shell\nallowed_commands:\n  - git\n  - jq\n');
  });

  it('omits the block when commands are empty', () => {
    const result = injectAllowedCommands('type: llm-chat\n', []);
    expect(result).toBe('type: llm-chat\n');
  });

  it('handles empty input YAML', () => {
    const result = injectAllowedCommands('', ['git']);
    expect(result).toBe('allowed_commands:\n  - git\n');
  });

  it('round-trips through split-then-inject', () => {
    const original = 'type: shell\nallowed_commands:\n  - git\n  - jq\n';
    const split = splitAllowedCommands(original);
    const reassembled = injectAllowedCommands(split.yaml, split.commands);
    expect(reassembled).toBe(original);
  });

  it('round-trips even when allowed_commands is reordered', () => {
    // Picker UX sorts on insertion; injection should write the order the
    // picker provided, regardless of the source order.
    const original = 'type: shell\nallowed_commands:\n  - rg\n  - git\n';
    const split = splitAllowedCommands(original);
    const reassembled = injectAllowedCommands(split.yaml, ['git', 'rg']);
    expect(reassembled).toBe(
      'type: shell\nallowed_commands:\n  - git\n  - rg\n',
    );
  });
});
