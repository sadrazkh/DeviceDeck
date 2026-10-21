import { forwardRef, type ReactNode } from 'react';
import { Button, type ButtonProps } from '../../components/Button';
import { Tooltip } from '../../components/ui';

/**
 * A button with a tooltip that can also open a menu: a menu's trigger hands its props and ref to the button,
 * through the tooltip's trigger.
 */
export const TipButton = forwardRef<HTMLButtonElement, ButtonProps & { tip: ReactNode; shortcut?: string }>(function TipButton({ tip, shortcut, ...button }, ref) {
  return (
    <Tooltip content={tip} shortcut={shortcut}>
      <Button ref={ref} {...button} />
    </Tooltip>
  );
});
