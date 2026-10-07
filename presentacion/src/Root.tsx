import React from 'react';
import {Composition} from 'remotion';
import {Presentacion} from './Presentacion';
import {FPS, H, TOTAL, W} from './theme';

export const RemotionRoot: React.FC = () => (
  <Composition id="Presentacion" component={Presentacion} durationInFrames={TOTAL} fps={FPS} width={W} height={H} />
);
