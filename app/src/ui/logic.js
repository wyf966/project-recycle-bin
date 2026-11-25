function isRealHash(hash) {
  return Boolean(hash && hash !== '~unique');
}

function extractExt(name) {
  if (!name) return '';
  const dot = name.lastIndexOf('.');
  if (dot <= 0 || dot === name.length - 1) return '';
  return name.slice(dot + 1).toLowerCase();
}

function formatBytes(bytes) {
  if (!Number.isFinite(bytes) || bytes < 0) return '0 B';
  if (bytes < 1024) return bytes + ' B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let idx = 0;
  while (value >= 1024 && idx < units.length - 1) {
    value /= 1024;
    idx++;
  }
  return (value >= 100 ? value.toFixed(0) : value.toFixed(1)) + ' ' + units[idx];
}

function groupItems(items, roots) {
  const count = items.length;
  const hashCounts = Object.create(null);
  for (let i = 0; i < count; i++) {
    const h = items[i].hash;
    if (isRealHash(h)) hashCounts[h] = (hashCounts[h] || 0) + 1;
  }

  const normRoots = [];
  if (roots && roots.length) {
    for (let i = 0; i < roots.length; i++) {
      const r = roots[i];
      if (!r) continue;
      const lower = r.toLowerCase();
      normRoots.push({ raw: r, lower: lower, prefix: lower + '\\', len: lower.length });
    }
    normRoots.sort((a, b) => b.len - a.len);
  }

  // Sort index array by deletedTs ascending so we don't mutate caller's array
  const indices = new Array(count);
  for (let i = 0; i < count; i++) indices[i] = i;
  indices.sort((a, b) => items[a].deletedTs - items[b].deletedTs);

  const dirProjectCache = new Map();
  const lastTs = Object.create(null);
  const batches = Object.create(null);
  const BATCH_GAP = 30 * 60 * 10000000; // 30 min in 100ns ticks
  const out = new Array(count);

  for (let i = 0; i < count; i++) {
    const x = items[indices[i]];
    const orig = x.original || '';
    const sep = orig.lastIndexOf('\\');
    const dir = sep >= 0 ? orig.slice(0, sep) : '';
    const dirLower = dir.toLowerCase();

    let project = dirProjectCache.get(dirLower);
    if (project === undefined) {
      project = dir;
      for (let r = 0; r < normRoots.length; r++) {
        const root = normRoots[r];
        if (dirLower === root.lower || dirLower.startsWith(root.prefix)) {
          project = root.raw;
          break;
        }
      }
      dirProjectCache.set(dirLower, project);
    }

    const prev = lastTs[dirLower];
    if (prev === undefined || x.deletedTs - prev > BATCH_GAP) {
      batches[dirLower] = (batches[dirLower] || 0) + 1;
    }
    lastTs[dirLower] = x.deletedTs;

    const ext = extractExt(x.name);
    const drive = orig.length >= 2 && orig[1] === ':' ? orig.slice(0, 2).toUpperCase() : '其他';
    const dupCount = isRealHash(x.hash) ? (hashCounts[x.hash] || 1) : 1;

    out[i] = {
      path: x.path,
      name: x.name,
      original: orig,
      hash: x.hash,
      category: x.category,
      size: x.size,
      bytes: x.bytes || 0,
      deletedTs: x.deletedTs || 0,
      modifiedTs: x.modifiedTs || 0,
      directory: dir,
      drive: drive,
      ext: ext,
      extLabel: ext ? ext.slice(0, 5).toUpperCase() : 'FILE',
      project: project,
      batch: dirLower + '#' + batches[dirLower],
      duplicates: dupCount,
      _nameLower: (x.name || '').toLowerCase(),
      _search: ((x.name || '') + '\n' + orig).toLowerCase()
    };
  }
  return out;
}

function parseSizeExpr(str) {
  const m = /^([<>]=?)\s*(\d+(?:\.\d+)?)\s*(b|kb|k|mb|m|gb|g|tb|t)?$/i.exec(str);
  if (!m) return null;
  const op = m[1];
  const val = parseFloat(m[2]);
  const unit = (m[3] || 'b').toLowerCase();
  const mult = unit === 't' || unit === 'tb' ? 1099511627776
    : unit === 'g' || unit === 'gb' ? 1073741824
    : unit === 'm' || unit === 'mb' ? 1048576
    : unit === 'k' || unit === 'kb' ? 1024 : 1;
  return { op: op, bytes: val * mult };
}

function parseSearchQuery(raw) {
  const trimmed = (raw || '').trim();
  if (!trimmed) return { active: false, terms: [], regexes: [], exts: [], sizeOps: [], drives: [], highlightTerms: [] };
  const tokens = trimmed.split(/\s+/);
  const terms = [], regexes = [], exts = [], sizeOps = [], drives = [], highlightTerms = [];

  for (let i = 0; i < tokens.length; i++) {
    const tok = tokens[i];
    const lower = tok.toLowerCase();
    if (lower.startsWith('ext:') && lower.length > 4) {
      exts.push(lower.slice(4).replace(/^\./, ''));
      continue;
    }
    if (lower.startsWith('drive:') && lower.length >= 7) {
      let d = lower.slice(6).toUpperCase();
      if (d.length === 1) d += ':';
      drives.push(d);
      continue;
    }
    const sizeRaw = lower.startsWith('size:') ? lower.slice(5) : lower;
    const sz = parseSizeExpr(sizeRaw);
    if (sz) {
      sizeOps.push(sz);
      continue;
    }
    if (tok.includes('*') || tok.includes('?')) {
      const escaped = lower.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*').replace(/\?/g, '.');
      try {
        regexes.push(new RegExp(escaped));
        continue;
      } catch {}
    }
    terms.push(lower);
    highlightTerms.push(tok);
  }
  return {
    active: terms.length > 0 || regexes.length > 0 || exts.length > 0 || sizeOps.length > 0 || drives.length > 0,
    terms: terms,
    regexes: regexes,
    exts: exts,
    sizeOps: sizeOps,
    drives: drives,
    highlightTerms: highlightTerms
  };
}

function matchesSearchQuery(item, q) {
  if (!q.active) return true;
  for (let i = 0; i < q.terms.length; i++) {
    if (!item._search.includes(q.terms[i])) return false;
  }
  for (let i = 0; i < q.exts.length; i++) {
    if (item.ext !== q.exts[i]) return false;
  }
  for (let i = 0; i < q.drives.length; i++) {
    if (item.drive !== q.drives[i]) return false;
  }
  for (let i = 0; i < q.sizeOps.length; i++) {
    const s = q.sizeOps[i];
    const b = item.bytes;
    if (s.op === '>' && !(b > s.bytes)) return false;
    if (s.op === '>=' && !(b >= s.bytes)) return false;
    if (s.op === '<' && !(b < s.bytes)) return false;
    if (s.op === '<=' && !(b <= s.bytes)) return false;
  }
  for (let i = 0; i < q.regexes.length; i++) {
    if (!q.regexes[i].test(item._search)) return false;
  }
  return true;
}

if (typeof module !== 'undefined') {
  module.exports = { groupItems, parseSearchQuery, matchesSearchQuery, formatBytes, extractExt, isRealHash };
}
