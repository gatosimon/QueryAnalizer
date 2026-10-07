/*
 * Genera la música de fondo de la presentación (public/musica.wav).
 *
 * Es una pieza original sintetizada por código, sin samples ni material de terceros, así que no tiene
 * derechos de autor. Estilo: electrónica suave y optimista ("tecnología corporativa"), 88 BPM, Do mayor.
 *
 * Estructura (cada barra dura ~2,7 s):
 *   barras  0-1   solo el colchón de acordes (pad), entrando de a poco
 *   barras  2-3   + bajo
 *   barras  4-7   + arpegio suave
 *   barras  8-23  + campanitas, bombo suave y shaker (la parte "llena")
 *   barras 24-27  + más presencia del arpegio (cierre del tema)
 *   barras 28-31  vuelve a quedar el pad y el bajo, con salida en fundido
 *
 * Uso:  node tools/generar-musica.js        (desde la carpeta presentacion)
 */
const fs = require('fs');
const path = require('path');

const SR = 44100;
const BPM = 88;
const BEAT = 60 / BPM;
const BAR = BEAT * 4;
const BARS = 32;
const DURACION = BARS * BAR; // ~87 s
const N = Math.ceil(DURACION * SR);

const L = new Float32Array(N);
const R = new Float32Array(N);
const sendL = new Float32Array(N); // lo que va al eco
const sendR = new Float32Array(N);

const midiAHz = (m) => 440 * Math.pow(2, (m - 69) / 12);

// Acordes (notas MIDI), cada uno dura 2 barras: Cmaj7 - Am7 - Fmaj7 - G6
const ACORDES = [
  {raiz: 36, notas: [60, 64, 67, 71], arp: [72, 76, 79, 83]}, // Cmaj7
  {raiz: 33, notas: [57, 60, 64, 67], arp: [69, 72, 76, 79]}, // Am7
  {raiz: 29, notas: [53, 57, 60, 64], arp: [65, 69, 72, 76]}, // Fmaj7
  {raiz: 31, notas: [55, 59, 62, 64], arp: [67, 71, 74, 76]}, // G6
];
const acordeDeBarra = (b) => ACORDES[Math.floor(b / 2) % 4];

/** Suma una nota con envolvente (ataque lineal, caída exponencial o sostenida con release). */
function nota({t0, dur, hz, amp, pan = 0, tipo = 'sine', ataque = 0.01, release = 0.1, caida = 0, eco = 0}) {
  const i0 = Math.floor(t0 * SR);
  const largo = Math.floor((dur + release) * SR);
  const gl = Math.cos(((pan + 1) * Math.PI) / 4);
  const gr = Math.sin(((pan + 1) * Math.PI) / 4);
  for (let k = 0; k < largo; k++) {
    const i = i0 + k;
    if (i < 0 || i >= N) continue;
    const t = k / SR;
    // envolvente
    let env = Math.min(1, t / ataque);
    if (caida > 0) env *= Math.exp(-t / caida);
    if (t > dur) env *= Math.max(0, 1 - (t - dur) / release);
    // timbre
    const w = 2 * Math.PI * hz * t;
    let s;
    if (tipo === 'pad') {
      s =
        Math.sin(w * 0.9965) * 0.5 +
        Math.sin(w * 1.0035) * 0.5 +
        Math.sin(w * 2) * 0.12 +
        Math.sin(w * 0.5) * 0.18;
    } else if (tipo === 'pluck') {
      s = Math.sin(w) + 0.32 * Math.sin(2 * w) + 0.14 * Math.sin(3 * w);
    } else if (tipo === 'bell') {
      s = Math.sin(w) + 0.45 * Math.sin(2.01 * w) + 0.2 * Math.sin(3.97 * w);
    } else {
      s = Math.sin(w);
    }
    const v = s * env * amp;
    L[i] += v * gl;
    R[i] += v * gr;
    if (eco > 0) {
      sendL[i] += v * gl * eco;
      sendR[i] += v * gr * eco;
    }
  }
}

/** Bombo suave: seno que baja de 120 a 46 Hz. */
function bombo(t0, amp) {
  const i0 = Math.floor(t0 * SR);
  const largo = Math.floor(0.32 * SR);
  let fase = 0;
  for (let k = 0; k < largo; k++) {
    const i = i0 + k;
    if (i >= N) break;
    const t = k / SR;
    const hz = 46 + 74 * Math.exp(-t * 28);
    fase += (2 * Math.PI * hz) / SR;
    const v = Math.sin(fase) * Math.exp(-t * 11) * amp;
    L[i] += v;
    R[i] += v;
  }
}

/** Shaker: ruido filtrado (pasa-altos simple) con caída muy corta. */
let semilla = 12345;
const azar = () => {
  semilla = (semilla * 1664525 + 1013904223) >>> 0;
  return semilla / 4294967296 - 0.5;
};
function shaker(t0, amp, pan) {
  const i0 = Math.floor(t0 * SR);
  const largo = Math.floor(0.07 * SR);
  const gl = Math.cos(((pan + 1) * Math.PI) / 4);
  const gr = Math.sin(((pan + 1) * Math.PI) / 4);
  let prev = 0;
  for (let k = 0; k < largo; k++) {
    const i = i0 + k;
    if (i >= N) break;
    const t = k / SR;
    const ruido = azar();
    const alto = ruido - prev;
    prev = ruido;
    const v = alto * Math.exp(-t * 55) * amp;
    L[i] += v * gl;
    R[i] += v * gr;
  }
}

// ───────── Arreglo ─────────
for (let b = 0; b < BARS; b++) {
  const t = b * BAR;
  const ac = acordeDeBarra(b);
  const primeraDelAcorde = b % 2 === 0;

  // Pad: una nota larga por acorde (2 barras), con ataque y salida lentos
  if (primeraDelAcorde) {
    const entrada = b === 0 ? 0.55 : 1; // el comienzo entra más bajito
    ac.notas.forEach((m, k) => {
      nota({
        t0: t,
        dur: BAR * 2 - 0.2,
        hz: midiAHz(m),
        amp: 0.085 * entrada,
        pan: -0.45 + k * 0.3,
        tipo: 'pad',
        ataque: 1.3,
        release: 1.8,
        eco: 0.25,
      });
    });
  }

  // Bajo: raíz en el tiempo 1 y en el "y" del 2 (síncopa suave)
  if (b >= 2) {
    const vol = b >= 28 ? 0.2 : 0.28;
    nota({t0: t, dur: BEAT * 1.4, hz: midiAHz(ac.raiz), amp: vol, tipo: 'sine', ataque: 0.02, release: 0.25});
    nota({t0: t + BEAT * 1.5, dur: BEAT * 0.9, hz: midiAHz(ac.raiz), amp: vol * 0.8, tipo: 'sine', ataque: 0.02, release: 0.2});
    if (b % 2 === 1 && b < 28) {
      nota({t0: t + BEAT * 3, dur: BEAT * 0.9, hz: midiAHz(ac.raiz + 7), amp: vol * 0.6, tipo: 'sine', ataque: 0.02, release: 0.2});
    }
  }

  // Arpegio en corcheas
  if (b >= 4 && b < 28) {
    const fuerte = b >= 8 ? 1 : 0.55;
    const extra = b >= 24 ? 1.2 : 1;
    const patron = [0, 1, 2, 3, 2, 1, 2, 1];
    for (let k = 0; k < 8; k++) {
      const m = ac.arp[patron[k]];
      nota({
        t0: t + (k * BEAT) / 2,
        dur: 0.05,
        hz: midiAHz(m),
        amp: (k % 4 === 0 ? 0.12 : 0.085) * fuerte * extra,
        pan: k % 2 === 0 ? -0.35 : 0.35,
        tipo: 'pluck',
        ataque: 0.004,
        release: 0.12,
        caida: 0.28,
        eco: 0.55,
      });
    }
  }

  // Campanitas: una nota larga y brillante cada barra, sobre la 9ª/5ª del acorde
  if (b >= 8 && b < 28 && b % 2 === 0) {
    const m = ac.arp[3] + (b % 4 === 0 ? 0 : -5);
    nota({
      t0: t + BEAT * 2,
      dur: 0.05,
      hz: midiAHz(m + 12),
      amp: 0.06,
      pan: b % 4 === 0 ? 0.5 : -0.5,
      tipo: 'bell',
      ataque: 0.004,
      release: 0.5,
      caida: 0.7,
      eco: 0.7,
    });
  }

  // Percusión suave
  if (b >= 8 && b < 28) {
    bombo(t, 0.34);
    bombo(t + BEAT * 2, 0.3);
    if (b >= 12) {
      for (let k = 0; k < 8; k++) {
        if (k % 2 === 1) shaker(t + (k * BEAT) / 2, 0.05, k % 4 === 1 ? -0.3 : 0.3);
      }
    }
  }
}

// ───────── Eco ping-pong (entre canales) ─────────
const d1 = Math.floor(BEAT * 0.75 * SR); // corchea con puntillo
const d2 = Math.floor(BEAT * 0.5 * SR);
const retroL = new Float32Array(N);
const retroR = new Float32Array(N);
for (let i = 0; i < N; i++) {
  const a = i >= d1 ? retroR[i - d1] * 0.42 : 0;
  const b = i >= d2 ? retroL[i - d2] * 0.36 : 0;
  retroL[i] = sendL[i] + a;
  retroR[i] = sendR[i] + b;
  L[i] += retroL[i] * 0.55;
  R[i] += retroR[i] * 0.55;
}

// ───────── Master: limitador suave, normalización y fundidos ─────────
let pico = 0;
for (let i = 0; i < N; i++) {
  L[i] = Math.tanh(L[i] * 1.25);
  R[i] = Math.tanh(R[i] * 1.25);
  pico = Math.max(pico, Math.abs(L[i]), Math.abs(R[i]));
}
const ganancia = 0.82 / pico;
const fadeIn = 1.5 * SR;
const fadeOut = 5 * SR;
for (let i = 0; i < N; i++) {
  let g = ganancia;
  if (i < fadeIn) g *= i / fadeIn;
  if (i > N - fadeOut) g *= (N - i) / fadeOut;
  L[i] *= g;
  R[i] *= g;
}

// ───────── WAV 16 bits estéreo ─────────
const datos = Buffer.alloc(N * 4);
for (let i = 0; i < N; i++) {
  datos.writeInt16LE(Math.max(-32768, Math.min(32767, Math.round(L[i] * 32767))), i * 4);
  datos.writeInt16LE(Math.max(-32768, Math.min(32767, Math.round(R[i] * 32767))), i * 4 + 2);
}
const cab = Buffer.alloc(44);
cab.write('RIFF', 0);
cab.writeUInt32LE(36 + datos.length, 4);
cab.write('WAVE', 8);
cab.write('fmt ', 12);
cab.writeUInt32LE(16, 16);
cab.writeUInt16LE(1, 20); // PCM
cab.writeUInt16LE(2, 22); // estéreo
cab.writeUInt32LE(SR, 24);
cab.writeUInt32LE(SR * 4, 28);
cab.writeUInt16LE(4, 32);
cab.writeUInt16LE(16, 34);
cab.write('data', 36);
cab.writeUInt32LE(datos.length, 40);

const destino = path.join(__dirname, '..', 'public', 'musica.wav');
fs.writeFileSync(destino, Buffer.concat([cab, datos]));
console.log(`Música generada: ${destino} (${DURACION.toFixed(1)} s, ${(fs.statSync(destino).size / 1048576).toFixed(1)} MB)`);
