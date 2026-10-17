/**
 * @fileoverview Tests for EditableCell component
 * @module Contacts/components/EditableCell/__tests__/EditableCell
 *
 * Story 3.4: Inline cell editing for contact fields
 */

import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { jest } from '@jest/globals';

// Mock MUI components
jest.mock('@mui/material', () => ({
  TextField: ({ inputProps, ...props }: any) => (
    <input
      data-testid="editable-cell-input"
      aria-label={inputProps?.['aria-label']}
      role={inputProps?.role}
      value={props.value}
      onChange={(e: any) => props.onChange?.({ target: { value: e.target.value } })}
      onKeyDown={props.onKeyDown}
      onBlur={props.onBlur}
      autoFocus={props.autoFocus}
    />
  ),
}));

// Mock logger
jest.mock('../../../../../shared/logger', () => ({
  logger: {
    info: jest.fn(),
    warn: jest.fn(),
    error: jest.fn(),
    debug: jest.fn(),
  },
}));

// Mock dispatch
const mockDispatch = jest.fn(() => Promise.resolve());
jest.mock('../../../types/hooks', () => ({
  useAppDispatch: () => mockDispatch,
}));

// Mock SyncStatusIcon
jest.mock('../../SyncStatusIcon', () => ({
  SyncStatusIcon: (props: any) => (
    <span
      data-testid="sync-status-icon"
      data-resource={props.resourceName}
      data-field={props.fieldPath}
    />
  ),
}));

// Mock executeFieldUpdate
const mockExecuteFieldUpdate = jest.fn(
  (args: { resourceName: string; fieldPath: string; newValue: unknown; oldValue: unknown }) => ({
    type: 'sync/executeFieldUpdate',
    payload: args,
  }),
);
jest.mock('../../../redux/thunks/syncThunks', () => ({
  executeFieldUpdate: (args: any) => mockExecuteFieldUpdate(args),
}));

import EditableCell from '../EditableCell';

const defaultProps = {
  resourceName: 'people/c12345',
  fieldPath: 'names',
  value: 'Alice Smith',
  fieldLabel: 'Name',
  contactName: 'Alice Smith',
};

describe('EditableCell', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  describe('ClickActivatesEditMode_Should_ShowInput_When_CellClicked', () => {
    it('should display value as text when not editing', () => {
      render(<EditableCell {...defaultProps} />);

      expect(screen.getByText('Alice Smith')).toBeInTheDocument();
      expect(screen.queryByTestId('editable-cell-input')).not.toBeInTheDocument();
    });

    it('should activate edit mode when cell is clicked', () => {
      render(<EditableCell {...defaultProps} />);

      fireEvent.click(screen.getByText('Alice Smith'));

      expect(screen.getByTestId('editable-cell-input')).toBeInTheDocument();
    });

    it('should pre-populate input with current value', () => {
      render(<EditableCell {...defaultProps} />);

      fireEvent.click(screen.getByText('Alice Smith'));

      const input = screen.getByTestId('editable-cell-input') as HTMLInputElement;
      expect(input.value).toBe('Alice Smith');
    });
  });

  describe('EnterKey_Should_SaveAndExitEditMode_When_Pressed', () => {
    it('should save and exit edit mode on Enter', () => {
      render(<EditableCell {...defaultProps} />);

      fireEvent.click(screen.getByText('Alice Smith'));
      const input = screen.getByTestId('editable-cell-input');

      fireEvent.change(input, { target: { value: 'Bob Jones' } });
      fireEvent.keyDown(input, { key: 'Enter' });

      expect(mockDispatch).toHaveBeenCalled();
      expect(mockExecuteFieldUpdate).toHaveBeenCalledWith({
        resourceName: 'people/c12345',
        fieldPath: 'names',
        newValue: 'Bob Jones',
        oldValue: 'Alice Smith',
      });
      expect(screen.queryByTestId('editable-cell-input')).not.toBeInTheDocument();
    });
  });

  describe('EscapeKey_Should_CancelWithoutSaving_When_Pressed', () => {
    it('should revert to original value and exit edit mode on Escape', () => {
      render(<EditableCell {...defaultProps} />);

      fireEvent.click(screen.getByText('Alice Smith'));
      const input = screen.getByTestId('editable-cell-input');

      fireEvent.change(input, { target: { value: 'Changed Value' } });
      fireEvent.keyDown(input, { key: 'Escape' });

      expect(mockDispatch).not.toHaveBeenCalled();
      expect(screen.queryByTestId('editable-cell-input')).not.toBeInTheDocument();
      expect(screen.getByText('Alice Smith')).toBeInTheDocument();
    });
  });

  describe('Blur_Should_SaveAndExitEditMode_When_ClickOutside', () => {
    it('should save on blur when value changed', () => {
      render(<EditableCell {...defaultProps} />);

      fireEvent.click(screen.getByText('Alice Smith'));
      const input = screen.getByTestId('editable-cell-input');

      fireEvent.change(input, { target: { value: 'New Name' } });
      fireEvent.blur(input);

      expect(mockDispatch).toHaveBeenCalled();
      expect(mockExecuteFieldUpdate).toHaveBeenCalledWith({
        resourceName: 'people/c12345',
        fieldPath: 'names',
        newValue: 'New Name',
        oldValue: 'Alice Smith',
      });
    });
  });

  describe('UnchangedValue_Should_NotDispatch_When_SaveTriggered', () => {
    it('should not dispatch if value is unchanged on Enter', () => {
      render(<EditableCell {...defaultProps} />);

      fireEvent.click(screen.getByText('Alice Smith'));
      const input = screen.getByTestId('editable-cell-input');

      fireEvent.keyDown(input, { key: 'Enter' });

      expect(mockDispatch).not.toHaveBeenCalled();
    });

    it('should not dispatch if value is unchanged on blur', () => {
      render(<EditableCell {...defaultProps} />);

      fireEvent.click(screen.getByText('Alice Smith'));
      const input = screen.getByTestId('editable-cell-input');

      fireEvent.blur(input);

      expect(mockDispatch).not.toHaveBeenCalled();
    });
  });

  describe('TabKey_Should_CallOnNavigateRight_When_Pressed', () => {
    it('should call onNavigate with right on Tab', () => {
      const mockNavigate = jest.fn();
      render(<EditableCell {...defaultProps} onNavigate={mockNavigate} />);

      fireEvent.click(screen.getByText('Alice Smith'));
      const input = screen.getByTestId('editable-cell-input');

      fireEvent.change(input, { target: { value: 'New Name' } });
      fireEvent.keyDown(input, { key: 'Tab' });

      expect(mockNavigate).toHaveBeenCalledWith('right');
    });
  });

  describe('EnterKey_Should_CallOnNavigateDown_When_Pressed', () => {
    it('should call onNavigate with down on Enter', () => {
      const mockNavigate = jest.fn();
      render(<EditableCell {...defaultProps} onNavigate={mockNavigate} />);

      fireEvent.click(screen.getByText('Alice Smith'));
      const input = screen.getByTestId('editable-cell-input');

      fireEvent.change(input, { target: { value: 'New Name' } });
      fireEvent.keyDown(input, { key: 'Enter' });

      expect(mockNavigate).toHaveBeenCalledWith('down');
    });
  });

  describe('AutoEdit_Should_EnterEditMode_When_PropTrue', () => {
    it('should auto-enter edit mode when autoEdit is true', () => {
      const mockConsumed = jest.fn();
      render(<EditableCell {...defaultProps} autoEdit={true} onAutoEditConsumed={mockConsumed} />);

      expect(screen.getByTestId('editable-cell-input')).toBeInTheDocument();
      expect(mockConsumed).toHaveBeenCalled();
    });

    it('should not auto-enter edit mode when autoEdit is false', () => {
      render(<EditableCell {...defaultProps} autoEdit={false} />);

      expect(screen.queryByTestId('editable-cell-input')).not.toBeInTheDocument();
      expect(screen.getByText('Alice Smith')).toBeInTheDocument();
    });
  });

  describe('Accessibility_Should_HaveAriaLabel_When_Editing', () => {
    it('should have correct aria-label on input', () => {
      render(<EditableCell {...defaultProps} />);

      fireEvent.click(screen.getByText('Alice Smith'));
      const input = screen.getByTestId('editable-cell-input');

      expect(input).toHaveAttribute('aria-label', 'Edit Name for Alice Smith');
    });

    it('should have role textbox on input', () => {
      render(<EditableCell {...defaultProps} />);

      fireEvent.click(screen.getByText('Alice Smith'));
      const input = screen.getByTestId('editable-cell-input');

      expect(input).toHaveAttribute('role', 'textbox');
    });
  });
});
