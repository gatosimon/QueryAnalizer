import {useCurrentFrame} from 'remotion';
import {SLOW} from './theme';

/**
 * Cuadro "de animación" de la escena actual: el cuadro real dividido por SLOW.
 * Las escenas usan este hook en lugar de useCurrentFrame para que todo corra más despacio
 * y, al terminar las animaciones, quede tiempo para leer.
 */
export const useFrame = (): number => useCurrentFrame() / SLOW;