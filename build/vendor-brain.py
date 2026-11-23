"""Vendors the Brain page's 3D graph library (3d-force-graph + three.js) into src/EzForensics.App/Assets/brain/vendor.

Run by hand when updating the pinned versions:  python build/vendor-brain.py
It downloads the pinned npm tarballs (and their dependencies) into a temp folder, follows the ES module imports from the entry
files, copies only the files that are reached plus each package's licence, and rewrites bare import specifiers ('three',
'kapsule', ...) to relative paths. The page then loads everything as module scripts from https://brain.local/ without an
import map, so the page's CSP stays script-src 'self'. There is exactly one three.js (build/three.module.js + three.core.js).
Unused code paths are replaced by small shims in vendor/shims (WebGPU renderer, the ngraph layout engine).
"""
import io, json, os, re, shutil, sys, tarfile, tempfile, urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, 'src', 'EzForensics.App', 'Assets', 'brain', 'vendor')

PINS = {
    '3d-force-graph': '1.80.1',
    'three': '0.186.1',
    'three-spritetext': '1.10.0',
    '@babel/runtime': '7.29.10',   # polished needs ^7
}

# entry files (package, path inside the package)
ENTRIES = [
    ('three', 'build/three.module.js'),
    ('3d-force-graph', 'dist/3d-force-graph.mjs'),
    ('three-spritetext', 'dist/three-spritetext.mjs'),
    ('three', 'examples/jsm/postprocessing/UnrealBloomPass.js'),
    ('three', 'examples/jsm/postprocessing/OutputPass.js'),
]

# bare specifiers that point at code the Brain never runs
SHIMS = {
    'three/webgpu': 'webgpu.js',
    'ngraph.forcelayout': 'ngraph-forcelayout.js',
}

IMPORT_RE = re.compile(r"""^(\s*(?:import|export)\b[^;'"]*?\bfrom\s*)(['"])([^'"]+)\2|^(\s*import\s*)(['"])([^'"]+)\5""", re.M)


def get(url):
    with urllib.request.urlopen(url) as r:
        return r.read()


def fetch(tmp):
    """Downloads the pinned packages and the latest matching dependency of each (sufficient for this set; checked by hand)."""
    seen, queue = {}, list(PINS.items())
    while queue:
        name, ver = queue.pop()
        if name in seen:
            continue
        ver = PINS.get(name, ver) or json.loads(get(f'https://registry.npmjs.org/{name}'))['dist-tags']['latest']
        meta = json.loads(get(f'https://registry.npmjs.org/{name}/{ver}'))
        seen[name] = ver
        dest = os.path.join(tmp, name)
        with tarfile.open(fileobj=io.BytesIO(get(meta['dist']['tarball'])), mode='r:gz') as t:
            for m in t.getmembers():
                rel = m.name.split('/', 1)[1] if '/' in m.name else m.name
                if not m.isfile() or '..' in rel.split('/'):
                    continue
                p = os.path.join(dest, rel)
                os.makedirs(os.path.dirname(p), exist_ok=True)
                with open(p, 'wb') as f:
                    f.write(t.extractfile(m).read())
        for dep in (meta.get('dependencies') or {}):
            if not (name == 'three-spritetext' and dep == 'three'):
                queue.append((dep, None))
    return seen


def split_bare(spec):
    parts = spec.split('/')
    n = 2 if spec.startswith('@') else 1
    return '/'.join(parts[:n]), '/'.join(parts[n:])


def package_entry(tmp, pkg):
    m = json.load(open(os.path.join(tmp, pkg, 'package.json'), encoding='utf-8'))
    exp = m.get('exports')
    if isinstance(exp, dict):
        root = exp.get('.', exp)
        if isinstance(root, str):
            return root
        for key in ('import', 'default', 'module'):
            if isinstance(root.get(key), str) and not root[key].endswith('.cjs'):
                return root[key]
    return m.get('module') or m.get('main') or 'index.js'


def resolve(tmp, from_pkg, from_rel, spec):
    """(package, path) of the file a specifier points at, or ('shims', file)."""
    if spec in SHIMS:
        return 'shims', SHIMS[spec]
    if spec.startswith('.'):
        pkg, base = from_pkg, os.path.normpath(os.path.join(os.path.dirname(from_rel), spec)).replace('\\', '/')
    else:
        pkg, sub = split_bare(spec)
        if pkg == 'three' and sub.startswith('addons/'):
            sub = 'examples/jsm/' + sub[len('addons/'):]
        base = sub or package_entry(tmp, pkg)
    if base.startswith('./'):
        base = base[2:]
    for cand in (base, base + '.js', base + '.mjs', base + '/index.js'):
        if os.path.isfile(os.path.join(tmp, pkg, cand)):
            return pkg, cand
    raise SystemExit(f'cannot resolve {spec!r} from {from_pkg}/{from_rel}')


def out_path(pkg, rel):
    # .mjs is saved as .js: WebView2's virtual host may not know the .mjs MIME type, and module scripts need a JavaScript one
    if rel.endswith('.mjs'):
        rel = rel[:-4] + '.js'
    return os.path.join(OUT, pkg, rel)


def vendor(tmp, versions):
    if os.path.isdir(OUT):
        for name in os.listdir(OUT):
            p = os.path.join(OUT, name)
            if name == 'shims':
                continue
            if os.path.isdir(p):
                shutil.rmtree(p)
            else:
                os.remove(p)
    done, todo, used = set(), list(ENTRIES), set()
    while todo:
        pkg, rel = todo.pop()
        if (pkg, rel) in done:
            continue
        done.add((pkg, rel))
        used.add(pkg)
        text = open(os.path.join(tmp, pkg, rel), encoding='utf-8').read()

        def repl(m):
            pre, q, spec = (m.group(1), m.group(2), m.group(3)) if m.group(1) is not None else (m.group(4), m.group(5), m.group(6))
            tpkg, trel = resolve(tmp, pkg, rel, spec)
            if tpkg != 'shims':
                todo.append((tpkg, trel))
            target = os.path.join(OUT, 'shims', trel) if tpkg == 'shims' else out_path(tpkg, trel)
            r = os.path.relpath(target, os.path.dirname(out_path(pkg, rel))).replace('\\', '/')
            return pre + q + (r if r.startswith('.') else './' + r) + q

        text = IMPORT_RE.sub(repl, text)
        dest = out_path(pkg, rel)
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        with open(dest, 'w', encoding='utf-8', newline='\n') as f:
            f.write(text)
    for pkg in sorted(used):
        for lic in ('LICENSE', 'LICENSE.md', 'LICENSE.txt', 'license'):
            p = os.path.join(tmp, pkg, lic)
            if os.path.isfile(p):
                shutil.copyfile(p, out_path(pkg, 'LICENSE'))
                break
        else:
            raise SystemExit(f'no licence file in {pkg}')
    with open(os.path.join(OUT, 'VERSIONS.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('Vendored by build/vendor-brain.py. Bare imports rewritten to relative paths and .mjs saved as .js; otherwise unchanged.\n')
        for pkg in sorted(used):
            f.write(f'{pkg} {versions[pkg]}\n')
    return sorted(used), len(done)


if __name__ == '__main__':
    tmp = tempfile.mkdtemp(prefix='ezf-brain-npm-')
    try:
        versions = fetch(tmp)
        used, files = vendor(tmp, versions)
        print(f'{files} files from {len(used)} packages -> {OUT}')
        for p in used:
            print(' ', p, versions[p])
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
