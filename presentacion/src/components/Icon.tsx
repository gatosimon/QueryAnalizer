import React from 'react';

/** Íconos vectoriales de QueryAnalyzer (grilla de 16×16, trazo redondeado), los mismos de `Icons.xaml`. */
const PATHS = {
  db: 'M3,3.5 A5,2 0 1 0 13,3.5 A5,2 0 1 0 3,3.5 Z M3,3.5 V12.5 C3,13.6 5.2,14.5 8,14.5 S13,13.6 13,12.5 V3.5 M3,8 C3,9.1 5.2,10 8,10 S13,9.1 13,8',
  search: 'M2.5,7 A4.5,4.5 0 1 0 11.5,7 A4.5,4.5 0 1 0 2.5,7 Z M10.5,10.5 L14,14',
  table: 'M2,3 H14 V13 H2 Z M2,6.5 H14 M2,10 H14 M6.5,3 V13',
  compare: 'M3,5 H11 M9,2.5 L11.5,5 L9,7.5 M13,11 H5 M7,8.5 L4.5,11 L7,13.5',
  transfer: 'M2.5,8 H13.5 M10,4.5 L13.5,8 L10,11.5',
  broom: 'M9.5,2.5 L12.5,5.5 M11,4 L6,9 M3,13 L4.5,9 L7,11.5 L3,13 Z',
  design: 'M2.5,13.5 H13.5 M4,11 L5,7.5 L11,1.8 L13.2,4 L7.5,10 Z',
  download: 'M8,2.5 V10.5 M4.5,7.5 L8,11 L11.5,7.5 M3,13.5 H13',
  moon: 'M12.5,9.5 A5,5 0 0 1 6.5,3.5 A5,5 0 1 0 12.5,9.5 Z',
  plug: 'M6,2 V5 M10,2 V5 M4.5,5 H11.5 V8 A3.5,3.5 0 0 1 4.5,8 Z M8,11.5 V14',
  play: 'M4.5,3 L12.5,8 L4.5,13 Z',
  history: 'M2.5,8 A5.5,5.5 0 1 0 4.3,4 M2.5,3 V6 H5.5 M8,5 V8.2 L10,9.5',
  check: 'M3,8.5 L6.5,12 L13,4.5',
  folder: 'M2,4.5 H6 L7.5,6 H14 V12.5 H2 Z',
  tools: 'M2.5,2.5 H7 V7 H2.5 Z M9,2.5 H13.5 V7 H9 Z M2.5,9 H7 V13.5 H2.5 Z M9,9 H13.5 V13.5 H9 Z',
  link: 'M6.5,9.5 L9.5,6.5 M7,5 L8,4 A2.4,2.4 0 0 1 11.4,7.4 L10.4,8.4 M9,11 L8,12 A2.4,2.4 0 0 1 4.6,8.6 L5.6,7.6',
  close: 'M4,4 L12,12 M12,4 L4,12',
  param: 'M6,3 C4,3 4,4 4,5.5 S4,8 2.5,8 C4,8 4,9 4,10.5 S4,13 6,13 M10,3 C12,3 12,4 12,5.5 S12,8 13.5,8 C12,8 12,9 12,10.5 S12,13 10,13',
} as const;

export type IconName = keyof typeof PATHS;

type Props = {
  name: IconName;
  size?: number;
  color?: string;
  stroke?: number;
  filled?: boolean;
  style?: React.CSSProperties;
};

export const Icon: React.FC<Props> = ({name, size = 48, color = 'currentColor', stroke = 1.3, filled, style}) => (
  <svg
    width={size}
    height={size}
    viewBox="0 0 16 16"
    fill={filled ? color : 'none'}
    stroke={color}
    strokeWidth={stroke}
    strokeLinecap="round"
    strokeLinejoin="round"
    style={style}
  >
    <path d={PATHS[name]} />
  </svg>
);
