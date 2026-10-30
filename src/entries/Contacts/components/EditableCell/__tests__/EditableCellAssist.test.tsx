import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import EditableCell, { type CellAssist } from '../EditableCell';

jest.mock('../../SyncStatusIcon', () => ({ SyncStatusIcon: () => null }));

const props = {
  resourceName: 'people/1',
  fieldPath: 'emailAddresses',
  value: '',
  fieldLabel: 'Email',
  contactName: 'Dana Cohen',
  autoEdit: true,
};

const assist = (run: CellAssist['run']): CellAssist => ({
  label: 'Find in mail',
  icon: <span>?</span>,
  run,
});

describe('EditableCell assist (AD-43)', () => {
  it('puts a found value into the text box; Enter saves it', async () => {
    const onCommit = jest.fn();
    render(
      <EditableCell
        {...props}
        onCommit={onCommit}
        assist={assist(async () => ({ value: 'dana@x.com' }))}
      />,
    );
    const box = screen.getByRole('textbox');
    fireEvent.click(screen.getByRole('button', { name: 'Find in mail' }));
    await waitFor(() => expect((box as HTMLInputElement).value).toBe('dana@x.com'));
    // Not saved until the Owner says so
    expect(onCommit).not.toHaveBeenCalled();
    fireEvent.keyDown(box, { key: 'Enter' });
    expect(onCommit).toHaveBeenCalledWith('dana@x.com');
  });

  it('offers choices without saving the empty box, and fills the one picked', async () => {
    const onCommit = jest.fn();
    render(
      <EditableCell
        {...props}
        onCommit={onCommit}
        assist={assist(async () => ({
          choices: [{ value: 'a@x.com', detail: 'as “Dana C”' }, { value: 'b@x.com' }],
        }))}
      />,
    );
    const box = screen.getByRole('textbox');
    fireEvent.click(screen.getByRole('button', { name: 'Find in mail' }));
    const choice = await screen.findByText('b@x.com');
    // The menu takes the focus: that is not the end of the edit
    fireEvent.blur(box);
    fireEvent.click(choice);
    expect(onCommit).not.toHaveBeenCalled();
    await waitFor(() =>
      expect((screen.getByRole('textbox') as HTMLInputElement).value).toBe('b@x.com'),
    );
  });

  it('says why nothing was found', async () => {
    const onClick = jest.fn();
    render(
      <EditableCell
        {...props}
        onCommit={jest.fn()}
        assist={assist(async () => ({
          note: 'No mail from “Dana Cohen”',
          action: { label: 'Allow reading email content (leaves this page)', onClick },
        }))}
      />,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Find in mail' }));
    expect(await screen.findByText('No mail from “Dana Cohen”')).toBeTruthy();
    fireEvent.click(screen.getByText('Allow reading email content (leaves this page)'));
    expect(onClick).toHaveBeenCalled();
  });

  it('drops an answer that arrives after the edit ended, and keeps what was typed', async () => {
    let answer: (r: { value: string }) => void = () => undefined;
    const onCommit = jest.fn();
    render(
      <EditableCell
        {...props}
        onCommit={onCommit}
        assist={assist(() => new Promise((r) => (answer = r)))}
      />,
    );
    const box = screen.getByRole('textbox') as HTMLInputElement;
    fireEvent.click(screen.getByRole('button', { name: 'Find in mail' }));
    fireEvent.change(box, { target: { value: 'typed@x.com' } });
    answer({ value: 'found@x.com' });
    await waitFor(() => expect(box.value).toBe('typed@x.com'));
    fireEvent.keyDown(box, { key: 'Escape' });
    expect(onCommit).not.toHaveBeenCalled();
  });

  it('runs from the keyboard with Alt+Down', async () => {
    render(
      <EditableCell
        {...props}
        onCommit={jest.fn()}
        assist={assist(async () => ({ value: 'dana@x.com' }))}
      />,
    );
    const box = screen.getByRole('textbox') as HTMLInputElement;
    fireEvent.keyDown(box, { key: 'ArrowDown', altKey: true });
    await waitFor(() => expect(box.value).toBe('dana@x.com'));
  });

  it('shows the button only while the box is empty', () => {
    render(<EditableCell {...props} onCommit={jest.fn()} assist={assist(async () => ({}))} />);
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'x' } });
    expect(screen.queryByRole('button', { name: 'Find in mail' })).toBeNull();
  });
});
