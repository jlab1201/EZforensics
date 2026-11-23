// Brain: the knowledge graph as a glowing 3D brain that floats slowly, as if in liquid.
// Uses 3d-force-graph 1.80.1 on three.js 0.186.1, vendored as ES modules in vendor/ (one three.js; versions in vendor/VERSIONS.txt,
// MIT licences next to each package). Nothing is loaded from the network.
// From the app: {type:"graph", data, reset}, {type:"focus", key}, {type:"clear"}, {type:"theme", dark}.
// To the app:   {type:"ready"}, {type:"select", key}, {type:"layout", positions:{key:[x,y,z]}} (only after the engine stops),
//               {type:"problem", reason:"webgl"}, and {type:"drop"} with files/folders as additional objects.
import * as THREE from './vendor/three/build/three.module.js';
import ForceGraph3D from './vendor/3d-force-graph/dist/3d-force-graph.js';
import SpriteText from './vendor/three-spritetext/dist/three-spritetext.js';
import { UnrealBloomPass } from './vendor/three/examples/jsm/postprocessing/UnrealBloomPass.js';
import { OutputPass } from './vendor/three/examples/jsm/postprocessing/OutputPass.js';
import { forceX, forceY, forceZ } from './vendor/d3-force-3d/src/index.js';

window.brainStarted = true;

// dark canvas: bright neon kinds; light canvas: deeper, saturated versions of the same hues (pastels vanish on white)
const DARK_COLORS = {
  technique: '#ff5c7a',
  tactic: '#ffb454',
  artifact: '#4fd1ff',
  eventid: '#a78bfa',
  tool: '#5eead4',
  category: '#7f9fc4',
  doc: '#94a3b8'
};
const LIGHT_COLORS = {
  technique: '#e11d48',
  tactic: '#d97706',
  artifact: '#0284c7',
  eventid: '#7c3aed',
  tool: '#0f766e',
  category: '#3b6ea5',
  doc: '#64748b'
};
let COLORS = DARK_COLORS;
const KIND_NAMES = { technique: 'ATT&CK technique', tactic: 'ATT&CK tactic', artifact: 'Artifact', eventid: 'Event ID', tool: 'Tool', category: 'Category' };

const BIG = 1500;               // above this many nodes the effects are reduced
const ORBIT_SECONDS = 150;      // one full turn of the camera
const IDLE_MS = 2000;           // auto-motion comes back this long after the pointer leaves (or the last interaction)
const RAMP_UP = 1.5;            // seconds to ease the motion back in
const RAMP_DOWN = 0.6;          // seconds to ease it out
const LABELS = 18;              // the biggest hubs always carry a label
const FIT_PAD = 1.0;

const host = window.chrome && window.chrome.webview;
const send = m => { if (host) host.postMessage(m); };

const el = document.getElementById('graph');
const tip = document.getElementById('tip');
const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');

// ------------------------------------------------------------------ state

let nodes = [], links = [];
let light = document.documentElement.dataset.theme === 'light';   // app theme (the 'theme' message)
COLORS = light ? LIGHT_COLORS : DARK_COLORS;
const glowBlend = () => light ? THREE.NormalBlending : THREE.AdditiveBlending;   // additive glow vanishes on a light canvas
const BG = () => light ? '#eef1f7' : '#0b0f1a';
let byKey = new Map(), neighbours = new Map(), linksOf = new Map();
let hover = null, selected = null;
let litNodes = new Set(), litLinks = new Set();
let labelled = new Set();
let big = false, hasGraph = false;
let density = 1;               // 1 for a small brain, less for a crowded one (halos and links are dimmer so the core does not burn white)
let engineRunning = false, autoFit = true, fitTimer = 0, fallbackTimer = 0;
let center = new THREE.Vector3(), viewRadius = 200;
let lastInteract = -Infinity, controlsActive = false, dragging = false, pointerInside = false;
let ramp = 0;                   // 0..1, eased into "motion"
let tween = null;               // camera flight
let clock = 0;
let drift = 0;                  // like ramp, but held at 0 while the simulation runs
let fg = null;
let fogBase = 0;                // fog density for the current view (dark theme value)
const fogScale = () => light ? 0 : 1;   // light: no fog (it only washes the far side out to white)
let worldPerPx = 1;             // world units per screen pixel at the brain's centre (set by fit)

const isDoc = n => n.kind === 'doc';
const colorOf = n => COLORS[n.kind] || COLORS.doc;
const radius = n => isDoc(n) ? (n.group === 'distilled' ? 1.15 : 1.7) : 3 + 2.3 * Math.min(Math.max(n.size || 1, 0.5), 6);
const shortLabel = s => s.length > 42 ? s.slice(0, 40) + '…' : s;
const interact = () => { lastInteract = performance.now(); };

function mix(a, b, t) { return new THREE.Color(a).lerp(new THREE.Color(b), t); }

// ------------------------------------------------------------------ textures

function canvasTexture(size, paint) {
  const c = document.createElement('canvas');
  c.width = c.height = size;
  paint(c.getContext('2d'), size);
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  return t;
}

// a soft round glow: the halo around a neuron, the dust motes and the sparks
const glowTexture = () => canvasTexture(128, (g, s) => {
  const r = g.createRadialGradient(s / 2, s / 2, 0, s / 2, s / 2, s / 2);
  r.addColorStop(0, 'rgba(255,255,255,1)');
  r.addColorStop(0.18, 'rgba(255,255,255,0.65)');
  r.addColorStop(0.45, 'rgba(255,255,255,0.16)');
  r.addColorStop(1, 'rgba(255,255,255,0)');
  g.fillStyle = r;
  g.fillRect(0, 0, s, s);
});

// dark: deep blue-violet "fluid", darker at the edges; light: a pale blue-grey wash, a little deeper at the edges
const backgroundTexture = () => canvasTexture(512, (g, s) => {
  const r = g.createRadialGradient(s * 0.5, s * 0.46, 0, s * 0.5, s * 0.5, s * 0.72);
  r.addColorStop(0, light ? '#ffffff' : '#191437');
  r.addColorStop(0.45, light ? '#f1f3f9' : '#0e1328');
  r.addColorStop(1, light ? '#dde2ec' : '#05070f');
  g.fillStyle = r;
  g.fillRect(0, 0, s, s);
});

// ------------------------------------------------------------------ WebGL or nothing

function hasWebGL() {
  try {
    const c = document.createElement('canvas');
    return !!(c.getContext('webgl2') || c.getContext('webgl'));
  } catch (e) { return false; }
}


// ------------------------------------------------------------------ materials (shared, so a highlight is a pointer swap)

const GEO_HUB = new THREE.SphereGeometry(1, 28, 20);
const GEO_DOC = new THREE.SphereGeometry(1, 14, 10);
const GEO_DOC_LOW = new THREE.SphereGeometry(1, 8, 6);
const mats = {};

function materialsFor(kind) {
  if (mats[kind] && mats[kind].density === density && mats[kind].light === light) return mats[kind];
  const c = new THREE.Color(COLORS[kind] || COLORS.doc);
  const glow = glowTexture();
  const doc = kind === 'doc';
  // light: little self-glow so the shading shows, and the halos only a faint tint (normal blending)
  const em = light ? 0.3 : 1, ho = light ? 0.45 : 1;
  const body = (emissive, opacity, col = c) => doc
    ? new THREE.MeshLambertMaterial({ color: col, emissive: col, emissiveIntensity: emissive * em, transparent: opacity < 1, opacity, depthWrite: opacity > 0.5 })
    : new THREE.MeshStandardMaterial({ color: col, emissive: col, emissiveIntensity: emissive * em, roughness: light ? 0.5 : 0.32, metalness: light ? 0.05 : 0.18, transparent: opacity < 1, opacity, depthWrite: opacity > 0.5 });
  // light: a node pushed back by a selection is solid but pale (see-through pastel spheres on white read as haze); dark: see-through
  const paled = mix(c, BG(), 0.72);
  const halo = opacity => new THREE.SpriteMaterial({ map: glow, color: c, transparent: true, opacity: opacity * ho, depthWrite: false, blending: glowBlend() });
  mats[kind] = doc
    ? { rest: body(0.12, light ? 1 : 0.7), lit: body(0.3, 1), active: body(0.45, 1), faded: light ? body(0.05, 1, paled) : body(0.05, 0.08),
        haloRest: halo(0.0), haloLit: halo(0.12), haloActive: halo(0.25), haloFaded: halo(0.0) }
    : { rest: body(0.22, 1), lit: body(0.4, 1), active: body(0.55, 1), faded: light ? body(0.06, 1, paled) : body(0.06, 0.15),
        haloRest: halo(0.07 * density), haloLit: halo(0.16), haloActive: halo(0.3), haloFaded: halo(0.0) };
  mats[kind].density = density;
  mats[kind].light = light;
  return mats[kind];
}

const noRaycast = () => {};
const haloScale = n => isDoc(n) ? 3.5 : 4;

function makeNode(n) {
  const g = new THREE.Group();
  const r = radius(n);
  const m = materialsFor(isDoc(n) ? 'doc' : n.kind);
  const mesh = new THREE.Mesh(isDoc(n) ? (big ? GEO_DOC_LOW : GEO_DOC) : GEO_HUB, m.rest);
  mesh.scale.setScalar(r);
  g.add(mesh);
  let halo = null;
  if (!isDoc(n) || !big) {
    // each neuron owns its halo material so it can pulse on its own
    halo = new THREE.Sprite(m.haloRest.clone());
    halo.scale.setScalar(r * haloScale(n));
    halo.raycast = noRaycast;
    g.add(halo);
  }
  // a random slow pulse: a soft flash every 7-18 s, at its own moment
  const seed = hashKey(n.key);
  n.__vis = { group: g, mesh, halo, label: null, r, m, base: 0, state: 'rest',
    pw: (Math.PI * 2) / (7 + 11 * frac(seed * 0.618)), pp: frac(seed * 0.377) * Math.PI * 2 };
  applyState(n);
  return g;
}

// rest, or with something hovered/selected: active (that node), lit (its neighbours) or faded (everything else)
function applyState(n) {
  const v = n.__vis;
  if (!v) return;
  const focus = !!(hover || selected);
  const state = n === hover || n === selected ? 'active' : !focus ? 'rest' : litNodes.has(n.key) ? 'lit' : 'faded';
  v.mesh.material = v.m[state];
  if (v.halo) {
    const src = state === 'active' ? v.m.haloActive : state === 'lit' ? v.m.haloLit : state === 'faded' ? v.m.haloFaded : v.m.haloRest;
    v.base = src.opacity;
    v.halo.material.opacity = src.opacity;
    v.halo.scale.setScalar(v.r * haloScale(n) * (state === 'active' ? 1.3 : 1));
  }
  // labels: the big hubs (unless faded), the focused node, and the concepts around a selected node
  v.state = state;
  const want = state === 'active';
  v.boost = state === 'active' ? 1.35 : 1;
  if (want) ensureLabel(n);
  if (v.label) { v.label.visible = want; placeLabel(n); }
}

function ensureLabel(n) {
  const v = n.__vis;
  if (!v || v.label) return v && v.label;
  const ink = light ? mix(colorOf(n), '#000000', 0.55) : mix(colorOf(n), '#ffffff', 0.45).multiplyScalar(0.85);
  const text = new SpriteText(shortLabel(n.label || ''), 1, '#' + ink.getHexString());
  text.fontFace = '"Segoe UI Variable Text", "Segoe UI", sans-serif';
  text.fontWeight = '600';
  text.fontSize = 64;
  text.backgroundColor = light ? 'rgba(255,255,255,0.85)' : 'rgba(6,8,18,0.72)';
  text.padding = [0.45, 0.2];
  text.borderRadius = 0.35;
  text.material.depthTest = false;
  text.material.depthWrite = false;
  text.material.opacity = light ? 0.95 : 0.75;
  text.material.sizeAttenuation = false;   // the same size on screen at any distance
  text.center.set(0.5, -0.25);              // just above the neuron
  text.renderOrder = 10;
  text.raycast = noRaycast;
  const wrap = new THREE.Group();
  wrap.add(text);
  wrap.userData.text = text;
  v.label = wrap;
  v.group.add(wrap);
  placeLabel(n);
  return wrap;
}

// labels are a fixed size on screen (about 12 px text), whatever the zoom
function placeLabel(n) {
  const v = n.__vis, w = v && v.label;
  if (!w || !fg) return;
  const k = 17 * 2 * Math.tan(fg.camera().fov * Math.PI / 360) / Math.max(200, el.clientHeight);
  w.scale.setScalar(k * (v.boost || 1));
  w.position.set(0, v.r * (v.boost > 1 ? 1.6 : 1.1), 0);
}

// ------------------------------------------------------------------ links: one LineSegments with a colour gradient per link

// normal blending: thousands of overlapping links would add up to white in the middle
const linkMat = new THREE.LineBasicMaterial({ vertexColors: true, transparent: true, depthWrite: false });
let linkObj = null, linkPos = null, linkCol = null;

function buildLinks(scene) {
  if (linkObj) { scene.remove(linkObj); linkObj.geometry.dispose(); }
  const L = links.length;
  linkPos = new Float32Array(Math.max(1, L) * 6);
  linkCol = new Float32Array(Math.max(1, L) * 8);
  links.forEach((l, i) => {
    const a = colorOf(l.sn), b = colorOf(l.tn);
    // a document end borrows half of its concept's colour, so every link carries some colour
    const ca = isDoc(l.sn) && !isDoc(l.tn) ? mix(a, b, 0.5) : new THREE.Color(a);
    const cb = isDoc(l.tn) && !isDoc(l.sn) ? mix(b, a, 0.5) : new THREE.Color(b);
    // light: a document's links take its concept's colour (grey document ends made a grey haze over the brain)
    if (light) {
      if (isDoc(l.sn) !== isDoc(l.tn)) { const hub = new THREE.Color(isDoc(l.sn) ? b : a); ca.copy(hub); cb.copy(hub); }
      ca.lerp(new THREE.Color('#0f172a'), 0.35); cb.lerp(new THREE.Color('#0f172a'), 0.35);
    }
    linkCol.set([ca.r, ca.g, ca.b, 0, cb.r, cb.g, cb.b, 0], i * 8);
  });
  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.BufferAttribute(linkPos, 3).setUsage(THREE.DynamicDrawUsage));
  geo.setAttribute('color', new THREE.BufferAttribute(linkCol, 4).setUsage(THREE.DynamicDrawUsage));
  geo.setDrawRange(0, L * 2);
  linkObj = new THREE.LineSegments(geo, linkMat);
  linkObj.frustumCulled = false;
  linkObj.renderOrder = -1;
  scene.add(linkObj);
}

function linkAlphas() {
  if (!linkCol) return;
  const focus = !!(hover || selected);
  const rest = (big ? 0.16 : 0.24) * density;
  links.forEach((l, i) => {
    // light: concept-to-concept links carry the structure, the many document links stay a thin coloured web
    const restA = light ? (l.kind === 'cooccur' ? 0.7 : 0.32) * Math.max(density, 0.6) : rest * (l.kind === 'cooccur' ? 1.25 : 1);
    let a = litLinks.has(l) ? (light ? 0.9 : 0.8) : focus ? (light ? 0.07 : 0.025) : restA;
    linkCol[i * 8 + 3] = a;
    linkCol[i * 8 + 7] = a;
  });
  linkObj.geometry.attributes.color.needsUpdate = true;
}

// positions come from the shown spot of each node (simulation + drift)
function writeLinks() {
  if (!linkObj) return;
  for (let i = 0; i < links.length; i++) {
    const s = links[i].sn, t = links[i].tn, o = i * 6;
    linkPos[o] = s.__px; linkPos[o + 1] = s.__py; linkPos[o + 2] = s.__pz;
    linkPos[o + 3] = t.__px; linkPos[o + 4] = t.__py; linkPos[o + 5] = t.__pz;
  }
  linkObj.geometry.attributes.position.needsUpdate = true;
}

// ------------------------------------------------------------------ sparks: light travelling along links (one Points object)

const SPARKS_MAX = 4096;
const sparkPos = new Float32Array(SPARKS_MAX * 3);
const sparkCol = new Float32Array(SPARKS_MAX * 3);
const sparks = [];   // {link, t, speed, reverse, flash}
let sparkObj = null, sparkMat = null, nextSpark = 0;

function buildSparks(scene) {
  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.BufferAttribute(sparkPos, 3).setUsage(THREE.DynamicDrawUsage));
  geo.setAttribute('color', new THREE.BufferAttribute(sparkCol, 3).setUsage(THREE.DynamicDrawUsage));
  geo.setDrawRange(0, 0);
  sparkMat = new THREE.PointsMaterial({ size: 6, map: glowTexture(), vertexColors: true, transparent: true, depthWrite: false, blending: glowBlend() });
  sparkObj = new THREE.Points(geo, sparkMat);
  sparkObj.frustumCulled = false;
  scene.add(sparkObj);
}

// directional particles on the links of the hovered or selected node
function resetLitSparks() {
  for (let i = sparks.length - 1; i >= 0; i--) if (!sparks[i].flash) sparks.splice(i, 1);
  const per = litLinks.size > 400 ? 1 : 2;
  litLinks.forEach(l => {
    for (let k = 0; k < per && sparks.length < SPARKS_MAX; k++) sparks.push({ link: l, t: k / per + Math.random() * 0.2, speed: 0.33 + Math.random() * 0.1, flash: false });
  });
}

// now and then a neuron fires: one spark along a random link
function fireSpark() {
  if (!links.length || sparks.length >= SPARKS_MAX) return;
  const l = links[(Math.random() * links.length) | 0];
  sparks.push({ link: l, t: 0, speed: 0.22 + Math.random() * 0.15, flash: true, reverse: Math.random() < 0.5 });
}

function stepSparks(dt, fireRate) {
  if (!sparkObj) return;
  nextSpark -= dt * fireRate;
  while (nextSpark <= 0 && fireRate > 0) { fireSpark(); nextSpark += 1; }
  let n = 0;
  for (let i = sparks.length - 1; i >= 0; i--) {
    const p = sparks[i];
    p.t += dt * p.speed;
    if (p.t >= 1) {
      if (p.flash) { sparks.splice(i, 1); continue; }
      p.t -= 1;
    }
    const s = p.reverse ? p.link.tn : p.link.sn, e = p.reverse ? p.link.sn : p.link.tn;
    const o = n * 3, t = p.t;
    sparkPos[o] = s.__px + (e.__px - s.__px) * t;
    sparkPos[o + 1] = s.__py + (e.__py - s.__py) * t;
    sparkPos[o + 2] = s.__pz + (e.__pz - s.__pz) * t;
    const c = p.color || (p.color = mix(colorOf(isDoc(e) ? s : e), light ? '#0f172a' : '#ffffff', light ? 0.15 : 0.35));
    const fade = p.flash ? Math.sin(Math.PI * t) : 1;   // fired sparks swell in and out
    // dark: fade to black (additive = nothing); light: fade to white (the canvas)
    if (light) { sparkCol[o] = 1 - (1 - c.r) * fade; sparkCol[o + 1] = 1 - (1 - c.g) * fade; sparkCol[o + 2] = 1 - (1 - c.b) * fade; }
    else { sparkCol[o] = c.r * fade; sparkCol[o + 1] = c.g * fade; sparkCol[o + 2] = c.b * fade; }
    n++;
  }
  sparkObj.geometry.setDrawRange(0, n);
  sparkObj.geometry.attributes.position.needsUpdate = true;
  sparkObj.geometry.attributes.color.needsUpdate = true;
}

// ------------------------------------------------------------------ dust: a faint field of motes around the brain

let dust = null;

function buildDust(scene) {
  if (dust) { scene.remove(dust); dust.geometry.dispose(); }
  const count = big ? 500 : 1400;
  const pos = new Float32Array(count * 3), col = new Float32Array(count * 3);
  const palette = Object.values(COLORS).map(c => new THREE.Color(c));
  for (let i = 0; i < count; i++) {
    // spread through a thick shell (unit radius 0.5..2.6) so the brain sits inside a cloud
    const u = Math.random() * 2 - 1, phi = Math.random() * Math.PI * 2;
    const r = 0.5 + Math.pow(Math.random(), 0.7) * 2.1, q = Math.sqrt(1 - u * u);
    pos.set([r * q * Math.cos(phi), r * u, r * q * Math.sin(phi)], i * 3);
    const c = light
      ? palette[(Math.random() * palette.length) | 0].clone().lerp(new THREE.Color('#ffffff'), 0.35 + Math.random() * 0.35)
      : palette[(Math.random() * palette.length) | 0].clone().lerp(new THREE.Color('#ffffff'), 0.3).multiplyScalar(0.25 + Math.random() * 0.35);
    col.set([c.r, c.g, c.b], i * 3);
  }
  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
  geo.setAttribute('color', new THREE.BufferAttribute(col, 3));
  dust = new THREE.Points(geo, new THREE.PointsMaterial({ size: 3, map: glowTexture(), vertexColors: true, transparent: true, opacity: light ? 0.35 : 0.3, depthWrite: false, blending: glowBlend() }));
  dust.frustumCulled = false;
  dust.visible = !light;   // pale motes over a pale canvas read as haze
  dust.renderOrder = -2;
  scene.add(dust);
  scaleDust();
}

function scaleDust() {
  if (!dust) return;
  dust.position.copy(center);
  dust.scale.setScalar(viewRadius);
  // a point's size is in world units at its depth: pixels * worldPerPx / tan(fov/2)
  const t = fg ? Math.tan(fg.camera().fov * Math.PI / 360) : 0.36;
  dust.material.size = 3 * worldPerPx / t;
  if (sparkMat) sparkMat.size = 11 * worldPerPx / t;
}

// ------------------------------------------------------------------ lights: soft coloured lamps circling the brain

const ambient = new THREE.AmbientLight(0x6070a0, 0.5);
const sun = new THREE.DirectionalLight(0xffffff, 0.35);
// dark: dim blue ambient + coloured lamps (a glow in the dark); light: daylight, so the coloured spheres read as solid shapes
function lightRig() {
  ambient.color.set(light ? 0xffffff : 0x6070a0);
  ambient.intensity = light ? 0.75 : 0.5;   // brighter washes the colours out to pastel
  sun.intensity = light ? 1.1 : 0.35;
  lamps.forEach(l => { if (l.base === undefined) l.base = l.light.intensity; l.light.intensity = l.base * (light ? 0.25 : 1); });
}

const lamps = [
  { light: new THREE.PointLight(0x4fd1ff, 0.7, 0, 0), speed: 0.05, phase: 0, tilt: 0.5 },
  { light: new THREE.PointLight(0xff5cc8, 0.55, 0, 0), speed: -0.035, phase: 2.1, tilt: -0.4 },
  { light: new THREE.PointLight(0xffb454, 0.4, 0, 0), speed: 0.026, phase: 4.2, tilt: 0.9 },
];

function moveLamps() {
  const R = viewRadius * 1.6;
  lamps.forEach(l => {
    const a = l.phase + clock * l.speed;
    l.light.position.set(center.x + R * Math.cos(a), center.y + R * Math.sin(a * 0.7) * l.tilt, center.z + R * Math.sin(a));
  });
}

// ------------------------------------------------------------------ the graph

function createGraph() {
  const g = new ForceGraph3D(el, { controlType: 'orbit', rendererConfig: { antialias: true, powerPreference: 'high-performance' } })
    .backgroundColor(BG())
    .showNavInfo(false)
    .nodeId('key')
    .nodeLabel(() => null)   // our own tooltip uses textContent (titles come from documents)
    .linkLabel(() => null)
    .nodeThreeObject(makeNode)
    .linkThreeObject(() => new THREE.Object3D())   // links are drawn as one LineSegments (buildLinks)
    .linkPositionUpdate(() => true)
    .linkDirectionalParticles(0)
    .enableNodeDrag(true)
    .d3VelocityDecay(0.32)
    .cooldownTime(15000)
    .onNodeHover(n => {
      hover = n || null;
      el.style.cursor = n ? 'pointer' : '';
      showTip(n);
      interact();
      updateHighlight();
    })
    .onNodeClick(n => {
      selected = n;
      interact();
      updateHighlight();
      send({ type: 'select', key: n.key });
      flyToNode(n, 1400);
    })
    .onBackgroundClick(() => {
      if (!selected) return;
      selected = null;
      interact();
      updateHighlight();
      send({ type: 'select', key: null });
      fit(1600);
    })
    .onNodeDrag(() => { dragging = true; interact(); })
    .onNodeDragEnd(() => { dragging = false; interact(); })
    .onEngineTick(onEngineTick)
    .onEngineStop(onEngineStop);

  g.lights([ambient, sun, ...lamps.map(l => l.light)]);
  lightRig();

  // hubs push harder than satellites, so concepts spread out and their documents orbit them
  g.d3Force('charge').strength(n => isDoc(n) ? -14 : -70 - 30 * (n.size || 1)).distanceMax(600);
  g.d3Force('link')
    .distance(l => l.kind === 'mention' ? 26 : l.kind === 'cooccur' ? 85 : l.kind === 'category' ? 60 : 42)
    .strength(l => l.kind === 'cooccur' ? 0.06 : l.kind === 'category' ? 0.08 : l.kind === 'mention' ? 0.35 : 0.2);
  // a gentle pull to the middle keeps loose pieces in the brain instead of drifting off (a rounder, more compact shape)
  g.d3Force('x', forceX(0).strength(0.012)).d3Force('y', forceY(0).strength(0.012)).d3Force('z', forceZ(0).strength(0.012));

  const scene = g.scene();
  scene.background = backgroundTexture();
  scene.fog = new THREE.FogExp2(BG(), 0);   // density set with the view size (depth cue)

  const controls = g.controls();
  controls.enableDamping = true;
  controls.dampingFactor = 0.06;
  controls.rotateSpeed = 0.6;
  controls.zoomSpeed = 0.8;
  controls.autoRotate = false;
  controls.addEventListener('start', () => { controlsActive = true; autoFit = false; tween = null; interact(); });
  controls.addEventListener('end', () => { controlsActive = false; interact(); });

  // glow
  const composer = g.postProcessingComposer();
  const bloom = new UnrealBloomPass(new THREE.Vector2(el.clientWidth || 800, el.clientHeight || 600), 0.45, 0.3, 0.5);
  composer.addPass(bloom);
  composer.addPass(new OutputPass());
  g.__bloom = bloom;
  bloom.enabled = !light;   // light: the bright canvas itself passes the threshold, so bloom laid a white haze over the whole graph
  [composer.renderTarget1, composer.renderTarget2].forEach(rt => { rt.samples = 4; rt.dispose(); });   // smooth lines under post-processing

  buildSparks(scene);
  return g;
}

// fewer and cheaper effects for a big brain
function tuneForSize() {
  if (!fg) return;
  const pr = Math.min(window.devicePixelRatio || 1, big ? 1 : 1.5);
  const renderer = fg.renderer();
  const composer = fg.postProcessingComposer();
  renderer.setPixelRatio(pr);
  composer.setPixelRatio(pr);
  fg.__bloom.strength = big ? 0.35 : 0.45;
  fg.__bloom.radius = big ? 0.25 : 0.3;
  fg.__bloom.threshold = big ? 0.6 : 0.5;
  resize();
}

// ------------------------------------------------------------------ highlight

function updateHighlight() {
  litNodes = new Set();
  litLinks = new Set();
  [hover, selected].forEach(n => {
    if (!n) return;
    litNodes.add(n.key);
    (neighbours.get(n.key) || []).forEach(k => litNodes.add(k));
    (linksOf.get(n.key) || []).forEach(l => litLinks.add(l));
  });
  nodes.forEach(applyState);
  linkAlphas();
  resetLitSparks();
}

// ------------------------------------------------------------------ camera

function graphSphere() {
  let minX = Infinity, minY = Infinity, minZ = Infinity, maxX = -Infinity, maxY = -Infinity, maxZ = -Infinity, any = false;
  nodes.forEach(n => {
    if (!isFinite(n.x)) return;
    any = true;
    const z = n.z || 0;
    if (n.x < minX) minX = n.x; if (n.x > maxX) maxX = n.x;
    if (n.y < minY) minY = n.y; if (n.y > maxY) maxY = n.y;
    if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
  });
  if (!any) return null;
  const c = new THREE.Vector3((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
  const ds = [];
  nodes.forEach(n => { if (isFinite(n.x)) ds.push(Math.hypot(n.x - c.x, n.y - c.y, (n.z || 0) - c.z) + radius(n) * 1.5); });
  ds.sort((a, b) => a - b);
  // the 98th percentile (a stray node or two may sit at the edge of the view rather than shrink the whole brain)
  const r = ds.length < 50 ? ds[ds.length - 1] : ds[Math.floor(ds.length * 0.98)] * 1.03;
  return { c, r: Math.max(r, 20) };
}

function easeInOut(t) { return t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2; }

function flyTo(pos, target, ms, user) {
  const camera = fg.camera(), controls = fg.controls();
  if (!ms || reducedMotion.matches) {
    camera.position.copy(pos);
    controls.target.copy(target);
    camera.lookAt(target);
    tween = null;
    return;
  }
  tween = { p0: camera.position.clone(), t0: controls.target.clone(), p1: pos.clone(), t1: target.clone(), start: performance.now(), ms, user: !!user };
}

function stepTween(now) {
  if (!tween) return;
  const k = Math.min(1, (now - tween.start) / tween.ms), e = easeInOut(k);
  const camera = fg.camera(), controls = fg.controls();
  camera.position.lerpVectors(tween.p0, tween.p1, e);
  controls.target.lerpVectors(tween.t0, tween.t1, e);
  camera.lookAt(controls.target);
  if (k >= 1) { if (tween.user) interact(); tween = null; }
}

// the whole brain, centred, filling the view (a bounding sphere, so it still fits while it turns)
function fit(ms) {
  if (!fg) return;
  const s = graphSphere();
  if (!s) return;
  center.copy(s.c);
  viewRadius = s.r;
  const camera = fg.camera(), controls = fg.controls();
  const vfov = camera.fov * Math.PI / 180;
  const hfov = 2 * Math.atan(Math.tan(vfov / 2) * camera.aspect);
  const dist = s.r / Math.sin(Math.min(vfov, hfov) / 2) * FIT_PAD;
  worldPerPx = 2 * dist * Math.tan(vfov / 2) / Math.max(200, el.clientHeight);
  const dir = camera.position.clone().sub(controls.target);
  if (dir.lengthSq() < 1e-6) dir.set(0, 0, 1);
  dir.normalize();
  flyTo(s.c.clone().addScaledVector(dir, dist), s.c, ms);
  nodes.forEach(placeLabel);
  scaleDust();
  fogBase = 0.55 / (dist + s.r);
  scene().fog.density = fogBase * fogScale();
}

function flyToNode(n, ms) {
  if (!fg || !isFinite(n.x)) return;
  autoFit = false;
  const camera = fg.camera();
  const p = new THREE.Vector3(n.__px ?? n.x, n.__py ?? n.y, n.__pz ?? (n.z || 0));
  const dir = camera.position.clone().sub(p);
  if (dir.lengthSq() < 1e-6) dir.set(0, 0, 1);
  dir.normalize();
  const dist = Math.min(camera.position.distanceTo(p), Math.max(80 + radius(n) * 10, viewRadius * 0.6));
  flyTo(p.clone().addScaledVector(dir, dist), p, ms, true);
}

const scene = () => fg.scene();

// ------------------------------------------------------------------ the engine

function onEngineTick() {
  engineRunning = true;
  // the simulation moves the nodes this frame: links follow right away (no drift while it runs)
  nodes.forEach(n => { n.__px = n.x; n.__py = n.y; n.__pz = n.z || 0; });
  writeLinks();
}

function onEngineStop() {
  engineRunning = false;
  clearInterval(fitTimer); fitTimer = 0;
  clearTimeout(fallbackTimer); fallbackTimer = 0;
  if (!hasGraph) return;
  if (autoFit) fit(1400);
  const positions = {};
  nodes.forEach(n => {
    if (isFinite(n.x) && isFinite(n.y) && isFinite(n.z)) positions[n.key] = [Math.round(n.x * 10) / 10, Math.round(n.y * 10) / 10, Math.round(n.z * 10) / 10];
  });
  send({ type: 'layout', positions });
}

// ------------------------------------------------------------------ the frame loop: drift, breathing, orbit, sparks

function phases(n) {
  if (n.__ph) return n.__ph;
  const r = () => Math.random() * Math.PI * 2, f = () => (Math.PI * 2) / (14 + Math.random() * 12);
  return (n.__ph = [r(), r(), r(), f(), f(), f()]);
}

let last = performance.now();

function frame(now) {
  requestAnimationFrame(frame);
  if (!fg) return;
  const dt = Math.min(0.05, (now - last) / 1000);
  last = now;
  clock += dt;

  // motion comes back slowly after the user lets go, and goes away quickly when they touch something
  // the graph holds still while the pointer is over it, and starts moving again a moment after it leaves
  const paused = reducedMotion.matches || pointerInside || dragging || controlsActive || (!!tween && tween.user) || !hasGraph;
  const wanted = !paused && now - lastInteract > IDLE_MS ? 1 : 0;
  ramp = wanted > ramp ? Math.min(1, ramp + dt / RAMP_UP) : Math.max(0, ramp - dt / RAMP_DOWN);
  const motion = ramp * ramp * (3 - 2 * ramp);
  drift = engineRunning ? 0 : Math.min(drift + dt / RAMP_UP, motion);

  stepTween(now);
  const controls = fg.controls();
  controls.autoRotate = motion > 0.001;
  controls.autoRotateSpeed = (60 / ORBIT_SECONDS) * motion;

  // drift: every node floats on its own slow sine path, and the whole brain swells and settles like breathing
  if (!engineRunning) {
    const amp = Math.min(5, Math.max(0.8, viewRadius * 0.012)) * drift;
    const breath = 0.009 * drift * Math.sin(clock * (Math.PI * 2) / 12);
    for (let i = 0; i < nodes.length; i++) {
      const n = nodes[i], o = n.__threeObj;
      if (!isFinite(n.x)) continue;
      let x = n.x, y = n.y, z = n.z || 0;
      if (amp > 0.001) {
        const p = phases(n);
        x += (x - center.x) * breath + amp * Math.sin(clock * p[3] + p[0]);
        y += (y - center.y) * breath + amp * Math.sin(clock * p[4] + p[1]);
        z += (z - center.z) * breath + amp * Math.sin(clock * p[5] + p[2]);
      }
      n.__px = x; n.__py = y; n.__pz = z;
      if (o) o.position.set(x, y, z);
    }
    writeLinks();
  }

  // random slow pulses of light on resting neurons
  if (!reducedMotion.matches) pulseNodes();

  // the selected neuron pulses
  if (selected && selected.__vis && selected.__vis.halo) {
    const v = selected.__vis;
    v.halo.scale.setScalar(v.r * haloScale(selected) * (1.3 + 0.18 * Math.sin(clock * 2.6)));
  }

  if (dust) { dust.rotation.y += dt * 0.012 * (0.3 + motion); dust.rotation.x = Math.sin(clock * 0.05) * 0.08; }
  moveLamps();
  stepSparks(dt, reducedMotion.matches ? 0 : (big ? 2.5 : 4) * (0.25 + 0.75 * motion));
}

// ------------------------------------------------------------------ pulses
function frac(x) { return x - Math.floor(x); }
function hashKey(k) { let h = 2166136261; for (let i = 0; i < k.length; i++) { h ^= k.charCodeAt(i); h = Math.imul(h, 16777619); } return (h >>> 0) / 4294967296 * 1000; }

function pulseNodes() {
  for (let i = 0; i < nodes.length; i++) {
    const v = nodes[i].__vis;
    if (!v || !v.halo || (v.state !== 'rest' && v.state !== 'lit')) continue;
    // a sharpened sine: dark most of the time, then a soft 1-2 s swell of light
    const s = Math.max(0, Math.sin(clock * v.pw + v.pp));
    const p = s * s * s * s * s * s;
    v.halo.material.opacity = v.base + p * (isDoc(nodes[i]) ? 0.22 : 0.32);
    v.halo.scale.setScalar(v.r * haloScale(nodes[i]) * (1 + 0.25 * p));
  }
}

// ------------------------------------------------------------------ size

let resizeTimer = 0;
function resize() {
  if (!fg) return;
  fg.width(el.clientWidth).height(el.clientHeight);
  nodes.forEach(placeLabel);
}
window.addEventListener('resize', () => {
  resize();
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(() => { if (hasGraph && !selected) fit(700); }, 250);
});

// ------------------------------------------------------------------ tooltip

const mouse = { x: 0, y: 0 };
el.addEventListener('pointermove', e => {
  mouse.x = e.clientX; mouse.y = e.clientY;
  if (!tip.hidden) placeTip();
});
el.addEventListener('pointerdown', interact);
el.addEventListener('pointerenter', () => { pointerInside = true; });
el.addEventListener('pointerleave', () => { pointerInside = false; interact(); });
document.documentElement.addEventListener('mouseleave', () => { pointerInside = false; interact(); });
window.addEventListener('blur', () => { if (pointerInside) { pointerInside = false; interact(); } });
el.addEventListener('wheel', () => { autoFit = false; tween = null; interact(); }, { passive: true });

function showTip(n) {
  if (!n) { tip.hidden = true; return; }
  tip.textContent = '';
  const dot = document.createElement('span');
  dot.className = 'dot';
  dot.style.background = colorOf(n);
  const name = document.createElement('span');
  name.textContent = n.label || '';
  const kind = document.createElement('span');
  kind.className = 'kind';
  kind.textContent = isDoc(n) ? (n.group || 'Document') : (KIND_NAMES[n.kind] || n.kind);
  tip.append(dot, name, kind);
  tip.hidden = false;
  placeTip();
}

function placeTip() {
  let x = mouse.x + 14, y = mouse.y + 14;
  const w = tip.offsetWidth, h = tip.offsetHeight;
  if (x + w > window.innerWidth - 4) x = mouse.x - w - 10;
  if (y + h > window.innerHeight - 4) y = mouse.y - h - 10;
  tip.style.left = x + 'px';
  tip.style.top = y + 'px';
}

// ------------------------------------------------------------------ messages

function setGraph(data, reset) {
  if (!fg) return;
  const old = new Map();
  if (!reset) nodes.forEach(n => old.set(n.key, n));
  let placed = 0;
  const next = (data.nodes || []).map(n => {
    const m = { key: n.key, kind: n.kind, label: n.label || '', group: n.group, size: n.size, noteId: n.noteId };
    const o = old.get(n.key);
    if (o && isFinite(o.x)) { m.x = o.x; m.y = o.y; m.z = o.z || 0; m.vx = m.vy = m.vz = 0; m.__ph = o.__ph; placed++; }
    else if (n.x != null && n.y != null && n.z != null) { m.x = n.x; m.y = n.y; m.z = n.z; placed++; }
    return m;
  });
  byKey = new Map(); neighbours = new Map(); linksOf = new Map();
  next.forEach(n => { byKey.set(n.key, n); neighbours.set(n.key, new Set()); linksOf.set(n.key, []); });
  const nextLinks = (data.links || [])
    .filter(l => byKey.has(l.source) && byKey.has(l.target) && l.source !== l.target)
    .map(l => ({ source: l.source, target: l.target, kind: l.kind, weight: l.weight, sn: byKey.get(l.source), tn: byKey.get(l.target) }));
  nextLinks.forEach(l => {
    neighbours.get(l.source).add(l.target); neighbours.get(l.target).add(l.source);
    linksOf.get(l.source).push(l); linksOf.get(l.target).push(l);
  });
  // until the simulation places them, nodes sit where they are known (or at the middle)
  next.forEach(n => { n.__px = isFinite(n.x) ? n.x : 0; n.__py = isFinite(n.y) ? n.y : 0; n.__pz = isFinite(n.z) ? n.z : 0; });

  big = next.length > BIG;
  density = Math.min(1, Math.max(0.35, Math.sqrt(600 / Math.max(1, next.length))));
  labelled = new Set(next.filter(n => !isDoc(n)).sort((a, b) => (b.size || 0) - (a.size || 0) || a.label.localeCompare(b.label)).slice(0, LABELS).map(n => n.key));
  nodes = next;
  links = nextLinks;
  hover = null;
  selected = selected ? byKey.get(selected.key) || null : null;
  tip.hidden = true;
  sparks.length = 0;
  const fresh = reset || !hasGraph;
  hasGraph = nodes.length > 0;
  if (fresh) autoFit = true;
  tuneForSize();

  buildLinks(scene());
  buildDust(scene());
  // a layout that is already known only needs to relax a little
  fg.d3AlphaDecay(nodes.length > 0 && placed === nodes.length ? 0.08 : 0.0228);
  engineRunning = true;
  fg.graphData({ nodes, links });
  updateHighlight();

  // centred and fitted: right away for a known layout, then while it settles, and once more when it stops (onEngineStop)
  clearInterval(fitTimer); clearTimeout(fallbackTimer);
  if (hasGraph && autoFit) {
    setTimeout(() => { if (autoFit) fit(placed === nodes.length ? 0 : 900); }, placed === nodes.length ? 60 : 500);
    fitTimer = setInterval(() => { if (autoFit && engineRunning) fit(1400); }, 1500);
    fallbackTimer = setTimeout(() => { clearInterval(fitTimer); if (autoFit) fit(1400); }, 17000);
  }
}

function focus(key, tries) {
  const n = byKey.get(key);
  if (!n || !fg) return;
  selected = n;
  interact();
  updateHighlight();
  if (!isFinite(n.x)) { if ((tries || 0) < 20) setTimeout(() => focus(key, (tries || 0) + 1), 100); return; }
  flyToNode(n, 1600);
}

function clearSelection() {
  if (!selected) return;
  selected = null;
  interact();
  updateHighlight();
  if (hasGraph) fit(1600);
}

if (host) {
  host.addEventListener('message', e => {
    const m = e.data;
    if (!m || typeof m !== 'object') return;
    if (m.type === 'graph' && m.data) setGraph(m.data, !!m.reset);
    else if (m.type === 'focus' && typeof m.key === 'string') focus(m.key, 0);
    else if (m.type === 'clear') clearSelection();
    else if (m.type === 'theme') applyTheme(!!m.dark);
  });
}

// Files and folders dropped on the graph: the app adds them to the Inbox (they wait there until accepted).
const dropEl = document.getElementById('drop');
const hasFiles = e => {
  const t = e.dataTransfer && e.dataTransfer.types;
  return !!t && Array.prototype.indexOf.call(t, 'Files') >= 0;
};
const showDrop = on => { if (dropEl) dropEl.hidden = !on; };
document.addEventListener('dragover', e => {
  if (!hasFiles(e)) return;
  e.preventDefault();
  if (e.dataTransfer) e.dataTransfer.dropEffect = 'copy';
  showDrop(true);
});
document.addEventListener('dragleave', e => { if (!e.relatedTarget) showDrop(false); });
document.addEventListener('drop', e => {
  e.preventDefault();
  showDrop(false);
  if (!host || !e.dataTransfer) return;
  const files = Array.prototype.slice.call(e.dataTransfer.files || []);
  const items = Array.prototype.slice.call(e.dataTransfer.items || []);
  // getAsFileSystemHandle gives folders too; start every call inside the event, before awaiting
  const pending = [];
  items.forEach(it => {
    if (it.kind === 'file' && typeof it.getAsFileSystemHandle === 'function') pending.push(it.getAsFileSystemHandle());
  });
  const post = objs => { if (objs.length) host.postMessageWithAdditionalObjects({ type: 'drop' }, objs); };
  if (!pending.length) { post(files); return; }
  Promise.all(pending.map(p => Promise.resolve(p).catch(() => null))).then(all => {
    const objs = all.filter(o => !!o);
    post(objs.length ? objs : files);
  });
});

// The app theme: canvas, fog, glow blending, labels and dust all follow it. Node materials are rebuilt on their next use.
function applyTheme(dark) {
  document.documentElement.dataset.theme = dark ? 'dark' : 'light';
  if (light === !dark) return;
  light = !dark;
  COLORS = light ? LIGHT_COLORS : DARK_COLORS;
  if (!fg) return;
  const sc = scene();
  sc.background = backgroundTexture();
  sc.fog.color.set(BG());
  sc.fog.density = fogBase * fogScale();
  fg.backgroundColor(BG());
  if (fg.__bloom) fg.__bloom.enabled = !light;
  lightRig();
  if (sparkMat) { sparkMat.blending = glowBlend(); sparkMat.needsUpdate = true; }
  sparks.forEach(p => { p.color = null; });
  buildDust(sc);
  buildLinks(sc);
  writeLinks();
  linkAlphas();
  nodes.forEach(n => {
    const v = n.__vis;
    if (!v) return;
    v.m = materialsFor(isDoc(n) ? 'doc' : n.kind);
    if (v.halo) { v.halo.material.dispose(); v.halo.material = v.m.haloRest.clone(); }
    if (v.label) { v.group.remove(v.label); v.label = null; }   // recreated in the new colours when shown
    applyState(n);
  });
}

// for a look in a plain browser while working on the page
window.brain = { setGraph, focus, clear: clearSelection, fit, theme: applyTheme, graph: () => fg, state: () => ({ center, viewRadius, worldPerPx, autoFit, engineRunning, ramp, drift }) };

if (!hasWebGL()) send({ type: 'problem', reason: 'webgl' });
else {
  try { fg = createGraph(); }
  catch (e) {
    console.error(e);
    fg = null;
    send({ type: 'problem', reason: 'webgl' });
  }
}
if (fg) {
  resize();
  requestAnimationFrame(frame);
}
send({ type: 'ready' });
