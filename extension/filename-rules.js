export const TAG_CATEGORIES = [
  ["work", "作品"], ["character", "角色"], ["artist", "作者"],
  ["kind", "类型"], ["rating", "分级"], ["custom", "自定义"],
];

export function matchFilename(rules, filename) {
  for (const rule of rules || []) {
    let match;
    try { match = new RegExp(rule.pattern, "i").exec(filename); }
    catch { continue; }
    if (!match) continue;
    const tags = {};
    for (const [index, category] of Object.entries(rule.groups || {})) {
      const value = match[Number(index)]?.trim();
      if (value && TAG_CATEGORIES.some(([key]) => key === category))
        (tags[category] ||= []).push(value);
    }
    return tags;
  }
  return null;
}
