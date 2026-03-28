/**
 * @fileoverview Tests for ConflictResolutionDialog component
 * @module Contacts/components/ConflictResolutionDialog/__tests__/ConflictResolutionDialog
 *
 * Story 3.7: Conflict resolution UI for 412 Precondition Failed
 */

import React from 'react';
import { render, screen, fireEvent } from '@testing-library/react';
import { jest } from '@jest/globals';

// Mock MUI components
jest.mock('@mui/material', () => ({
  Dialog: ({ children, ...props }: any) => (
    <div data-testid={props['data-testid'] || 'dialog'} role="dialog">
      {children}
    </div>
  ),
  DialogTitle: ({ children }: any) => <div data-testid="dialog-title">{children}</div>,
  DialogContent: ({ children }: any) => <div data-testid="dialog-content">{children}</div>,
  DialogActions: ({ children }: any) => <div data-testid="dialog-actions">{children}</div>,
  Button: ({ children, onClick, ...props }: any) => (
    <button onClick={onClick} data-testid={props['data-testid']}>
      {children}
    </button>
  ),
  Typography: ({ children, ...props }: any) => (
    <span data-testid={props['data-testid']}>{children}</span>
  ),
  Box: ({ children }: any) => <div>{children}</div>,
}));

import { ConflictResolutionDialog } from '../ConflictResolutionDialog';
import type { SyncOperation } from '../../../types/SyncOperation';

const createMockOperation = (overrides?: Partial<SyncOperation>): SyncOperation => ({
  id: 'op-conflict-1',
  resourceName: 'people/c123',
  fieldPath: 'names',
  oldValue: 'John Doe',
  newValue: 'Jane Doe',
  status: 'conflict',
  timestamp: '2026-03-28T10:00:00Z',
  error: null,
  remoteValue: 'Johnny Doe',
  ...overrides,
});

describe('ConflictResolutionDialog', () => {
  const mockOnKeepMine = jest.fn();
  const mockOnUseRemote = jest.fn();

  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('render_Should_ShowDialogTitle_When_ConflictExists', () => {
    const operation = createMockOperation();
    render(
      <ConflictResolutionDialog
        operation={operation}
        remoteValue="Johnny Doe"
        onKeepMine={mockOnKeepMine}
        onUseRemote={mockOnUseRemote}
      />,
    );

    expect(screen.getByText('Contact Modified Elsewhere')).toBeDefined();
  });

  it('render_Should_ShowOldValue_When_ConflictExists', () => {
    const operation = createMockOperation({ oldValue: 'Original Name' });
    render(
      <ConflictResolutionDialog
        operation={operation}
        remoteValue="Remote Name"
        onKeepMine={mockOnKeepMine}
        onUseRemote={mockOnUseRemote}
      />,
    );

    expect(screen.getByTestId('conflict-old-value').textContent).toBe('Original Name');
  });

  it('render_Should_ShowNewValue_When_ConflictExists', () => {
    const operation = createMockOperation({ newValue: 'My Edit' });
    render(
      <ConflictResolutionDialog
        operation={operation}
        remoteValue="Remote Name"
        onKeepMine={mockOnKeepMine}
        onUseRemote={mockOnUseRemote}
      />,
    );

    expect(screen.getByTestId('conflict-new-value').textContent).toBe('My Edit');
  });

  it('render_Should_ShowRemoteValue_When_ConflictExists', () => {
    const operation = createMockOperation();
    render(
      <ConflictResolutionDialog
        operation={operation}
        remoteValue="Remote Updated Name"
        onKeepMine={mockOnKeepMine}
        onUseRemote={mockOnUseRemote}
      />,
    );

    expect(screen.getByTestId('conflict-remote-value').textContent).toBe('Remote Updated Name');
  });

  it('render_Should_ShowFieldPath_When_ConflictExists', () => {
    const operation = createMockOperation({ fieldPath: 'emailAddresses' });
    render(
      <ConflictResolutionDialog
        operation={operation}
        remoteValue="remote@example.com"
        onKeepMine={mockOnKeepMine}
        onUseRemote={mockOnUseRemote}
      />,
    );

    expect(screen.getByText('Field: emailAddresses')).toBeDefined();
  });

  it('render_Should_ShowEmptyPlaceholder_When_ValueIsNull', () => {
    const operation = createMockOperation({ oldValue: null });
    render(
      <ConflictResolutionDialog
        operation={operation}
        remoteValue={null}
        onKeepMine={mockOnKeepMine}
        onUseRemote={mockOnUseRemote}
      />,
    );

    expect(screen.getByTestId('conflict-old-value').textContent).toBe('(empty)');
    expect(screen.getByTestId('conflict-remote-value').textContent).toBe('(empty)');
  });

  it('render_Should_ShowJsonStringified_When_ValueIsObject', () => {
    const operation = createMockOperation({
      oldValue: [{ givenName: 'John' }],
      newValue: [{ givenName: 'Jane' }],
    });
    render(
      <ConflictResolutionDialog
        operation={operation}
        remoteValue={[{ givenName: 'Johnny' }]}
        onKeepMine={mockOnKeepMine}
        onUseRemote={mockOnUseRemote}
      />,
    );

    expect(screen.getByTestId('conflict-old-value').textContent).toBe(
      JSON.stringify([{ givenName: 'John' }]),
    );
    expect(screen.getByTestId('conflict-new-value').textContent).toBe(
      JSON.stringify([{ givenName: 'Jane' }]),
    );
    expect(screen.getByTestId('conflict-remote-value').textContent).toBe(
      JSON.stringify([{ givenName: 'Johnny' }]),
    );
  });

  it('onKeepMine_Should_CallHandler_When_KeepYourChangeClicked', () => {
    const operation = createMockOperation();
    render(
      <ConflictResolutionDialog
        operation={operation}
        remoteValue="Remote Name"
        onKeepMine={mockOnKeepMine}
        onUseRemote={mockOnUseRemote}
      />,
    );

    fireEvent.click(screen.getByTestId('conflict-keep-mine-btn'));
    expect(mockOnKeepMine).toHaveBeenCalledTimes(1);
  });

  it('onUseRemote_Should_CallHandler_When_UseCurrentValueClicked', () => {
    const operation = createMockOperation();
    render(
      <ConflictResolutionDialog
        operation={operation}
        remoteValue="Remote Name"
        onKeepMine={mockOnKeepMine}
        onUseRemote={mockOnUseRemote}
      />,
    );

    fireEvent.click(screen.getByTestId('conflict-use-remote-btn'));
    expect(mockOnUseRemote).toHaveBeenCalledTimes(1);
  });

  it('render_Should_ShowBothButtons_When_ConflictExists', () => {
    const operation = createMockOperation();
    render(
      <ConflictResolutionDialog
        operation={operation}
        remoteValue="Remote Name"
        onKeepMine={mockOnKeepMine}
        onUseRemote={mockOnUseRemote}
      />,
    );

    expect(screen.getByText('Keep Your Change')).toBeDefined();
    expect(screen.getByText('Use Current Value')).toBeDefined();
  });

  it('render_Should_ShowDescriptionText_When_ConflictExists', () => {
    const operation = createMockOperation();
    render(
      <ConflictResolutionDialog
        operation={operation}
        remoteValue="Remote Name"
        onKeepMine={mockOnKeepMine}
        onUseRemote={mockOnUseRemote}
      />,
    );

    expect(
      screen.getByText('This contact was modified elsewhere. Choose which value to keep:'),
    ).toBeDefined();
  });
});
