/**
 * @fileoverview Tests for EditableCell component
 * @module Contacts/components/EditableCell/__tests__/EditableCell
 *
 * Story 3.4: inline cell editing. Story 4.4: in-place editor, double-click opens the card.
 */

import React from 'react';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { jest } from '@jest/globals';

// Mock SyncStatusIcon (needs a store; not under test here)
jest.mock('../../SyncStatusIcon', () => ({
  SyncStatusIcon: (props: { resourceName: string; fieldPath: string }) => (
    <span
      data-testid="sync-status-icon"
      data-resource={props.resourceName}
      data-field={props.fieldPath}
    />
  ),
}));

import EditableCell, { DOUBLE_CLICK_WINDOW_MS } from '../EditableCell';

const onCommit = jest.fn();
const onOpenCard = jest.fn();
const onNavigate = jest.fn();

const defaultProps = {
  resourceName: 'people/c12345',
  fieldPath: 'names',
  value: 'John Doe',
  fieldLabel: 'Name',
  contactName: 'John Doe',
  onCommit,
  onOpenCard,
  onNavigate,
};

/** Click once and let the double-click window pass */
const clickToEdit = (text = 'John Doe') => {
  fireEvent.click(screen.getByText(text), { detail: 1 });
  act(() => {
    jest.advanceTimersByTime(DOUBLE_CLICK_WINDOW_MS + 10);
  });
  return screen.getByRole('textbox') as HTMLInputElement;
};

describe('EditableCell', () => {
  beforeEach(() => {
    jest.useFakeTimers();
    jest.clearAllMocks();
  });

  afterEach(() => {
    jest.useRealTimers();
  });

  it('render_Should_ShowValueAndSyncIcon_When_NotEditing', () => {
    render(<EditableCell {...defaultProps} />);

    expect(screen.getByText('John Doe')).toBeTruthy();
    expect(screen.queryByRole('textbox')).toBeNull();
    expect(screen.getByTestId('sync-status-icon').getAttribute('data-field')).toBe('names');
  });

  it('render_Should_ShowPlaceholder_When_ValueEmpty', () => {
    render(<EditableCell {...defaultProps} value="" placeholder="Add phonetic" />);

    expect(screen.getByText('Add phonetic')).toBeTruthy();
  });

  it('click_Should_EnterEditMode_AfterDoubleClickWindow', () => {
    render(<EditableCell {...defaultProps} />);

    fireEvent.click(screen.getByText('John Doe'), { detail: 1 });
    expect(screen.queryByRole('textbox')).toBeNull();

    act(() => {
      jest.advanceTimersByTime(DOUBLE_CLICK_WINDOW_MS + 10);
    });
    const input = screen.getByRole('textbox') as HTMLInputElement;
    expect(input.value).toBe('John Doe');
    expect(input.getAttribute('aria-label')).toBe('Edit Name for John Doe');
  });

  it('doubleClick_Should_OpenCard_AndNotEdit', () => {
    render(<EditableCell {...defaultProps} />);
    const text = screen.getByText('John Doe');

    fireEvent.click(text, { detail: 1 });
    fireEvent.click(text, { detail: 2 });
    fireEvent.doubleClick(text);
    act(() => {
      jest.advanceTimersByTime(DOUBLE_CLICK_WINDOW_MS + 10);
    });

    expect(onOpenCard).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('textbox')).toBeNull();
  });

  it('editValue_Should_SeedTheEditor_When_DifferentFromDisplay', () => {
    render(<EditableCell {...defaultProps} value="Dr. John Doe" editValue="John Doe" />);

    const input = clickToEdit('Dr. John Doe');
    expect(input.value).toBe('John Doe');
  });

  it('enter_Should_CommitChangedText_AndNavigateDown', () => {
    render(<EditableCell {...defaultProps} />);
    const input = clickToEdit();

    fireEvent.change(input, { target: { value: 'Jane Doe' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(onCommit).toHaveBeenCalledWith('Jane Doe');
    expect(onNavigate).toHaveBeenCalledWith('down');
    expect(screen.queryByRole('textbox')).toBeNull();
  });

  it('tab_Should_CommitAndNavigateRight', () => {
    render(<EditableCell {...defaultProps} />);
    const input = clickToEdit();

    fireEvent.change(input, { target: { value: 'Jane' } });
    fireEvent.keyDown(input, { key: 'Tab' });

    expect(onCommit).toHaveBeenCalledWith('Jane');
    expect(onNavigate).toHaveBeenCalledWith('right');
  });

  it('escape_Should_CancelWithoutCommit', () => {
    render(<EditableCell {...defaultProps} />);
    const input = clickToEdit();

    fireEvent.change(input, { target: { value: 'Something else' } });
    fireEvent.keyDown(input, { key: 'Escape' });
    fireEvent.blur(input);

    expect(onCommit).not.toHaveBeenCalled();
    expect(screen.getByText('John Doe')).toBeTruthy();
  });

  it('blur_Should_Commit_When_Changed', () => {
    render(<EditableCell {...defaultProps} />);
    const input = clickToEdit();

    fireEvent.change(input, { target: { value: 'Jane Doe' } });
    fireEvent.blur(input);

    expect(onCommit).toHaveBeenCalledTimes(1);
    expect(onCommit).toHaveBeenCalledWith('Jane Doe');
  });

  it('save_Should_NotCommit_When_OnlyWhitespaceChanged', () => {
    render(<EditableCell {...defaultProps} />);
    const input = clickToEdit();

    fireEvent.change(input, { target: { value: '  John Doe ' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(onCommit).not.toHaveBeenCalled();
  });

  it('enterThenBlur_Should_CommitOnce', () => {
    render(<EditableCell {...defaultProps} />);
    const input = clickToEdit();

    fireEvent.change(input, { target: { value: 'Jane Doe' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    fireEvent.blur(input);

    expect(onCommit).toHaveBeenCalledTimes(1);
  });

  it('autoEdit_Should_EnterEditMode_AndNotifyConsumed', () => {
    const onAutoEditConsumed = jest.fn();
    render(<EditableCell {...defaultProps} autoEdit onAutoEditConsumed={onAutoEditConsumed} />);

    expect((screen.getByRole('textbox') as HTMLInputElement).value).toBe('John Doe');
    expect(onAutoEditConsumed).toHaveBeenCalled();
  });
});
