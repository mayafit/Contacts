import React from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import type { Contact } from '../../../types/Contact';
import TrashView from '../TrashView';

const person = (id: string, displayName: string) =>
  ({ resourceName: id, names: [{ displayName }] }) as Contact;
const trashed = [person('people/1', 'Avi Levi'), person('people/2', 'רחל שרמן')];

describe('Trash', () => {
  it('ticks and lists first the matches it was opened with; Restore acts on them only', () => {
    const onRestore = jest.fn();
    render(
      <TrashView
        trashed={trashed}
        onRestore={onRestore}
        onDeleteForever={jest.fn()}
        preselect={['people/2']}
      />,
    );
    const boxes = screen.getAllByRole('checkbox', { name: /^Check (?!all)/ });
    expect(boxes[0].getAttribute('aria-label')).toBe('Check רחל שרמן');
    expect((boxes[0] as HTMLInputElement).checked).toBe(true);
    expect((boxes[1] as HTMLInputElement).checked).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: 'Restore 1' }));
    expect(onRestore).toHaveBeenCalledWith([trashed[1]]);
  });

  it('opened from its tab, nothing is ticked', () => {
    render(<TrashView trashed={trashed} onRestore={jest.fn()} onDeleteForever={jest.fn()} />);
    expect(screen.getByRole('button', { name: 'Restore all' })).toBeTruthy();
  });
});
