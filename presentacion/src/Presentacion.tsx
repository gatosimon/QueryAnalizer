import React from 'react';
import {AbsoluteFill, Audio, Series, interpolate, staticFile} from 'remotion';
import {FPS, REAL, TOTAL} from './theme';
import {Background} from './components/Background';
import {Portada} from './scenes/Portada';
import {Problema} from './scenes/Problema';
import {Funcionalidades} from './scenes/Funcionalidades';
import {Arquitectura} from './scenes/Arquitectura';
import {Cierre} from './scenes/Cierre';

/** Volumen de la música: entra en 2 s, queda de fondo (45 %) y se apaga en los últimos 4 s. */
const volumenMusica = (frame: number): number =>
  interpolate(frame, [0, 2 * FPS, TOTAL - 4 * FPS, TOTAL], [0, 0.45, 0.45, 0], {
    extrapolateLeft: 'clamp',
    extrapolateRight: 'clamp',
  });

/** Presentación completa: el fondo animado va fijo, las escenas se suceden con fundidos y suena la música de fondo. */
export const Presentacion: React.FC = () => (
  <AbsoluteFill>
    <Audio src={staticFile('musica.wav')} volume={volumenMusica} />
    <Background />
    <Series>
      <Series.Sequence durationInFrames={REAL.portada}>
        <Portada />
      </Series.Sequence>
      <Series.Sequence durationInFrames={REAL.problema}>
        <Problema />
      </Series.Sequence>
      <Series.Sequence durationInFrames={REAL.funcionalidades}>
        <Funcionalidades />
      </Series.Sequence>
      <Series.Sequence durationInFrames={REAL.arquitectura}>
        <Arquitectura />
      </Series.Sequence>
      <Series.Sequence durationInFrames={REAL.cierre}>
        <Cierre />
      </Series.Sequence>
    </Series>
  </AbsoluteFill>
);
