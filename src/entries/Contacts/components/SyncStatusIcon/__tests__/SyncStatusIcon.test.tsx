/**
 * @fileoverview Tests for SyncStatusIcon component
 * @module Contacts/components/SyncStatusIcon/__tests__/SyncStatusIcon
 *
 * Story 3.5: Real-time sync status indicators
 */

import React from 'react';
import { render, screen, fireEvent, act } from '@testing-library/react';
import { jest } from '@jest/globals';
import type { SyncOperation } from '../../../types/SyncOperation';

// ---- Mocks ----

// Mock MUI components
jest.mock('@mui/material', () => ({
  Box: ({ children, ...props }: any) => (
    <div data-testid={props['data-testid']} {...props}>
      {children}
    </div>
  ),
  CircularProgress: (props: any) => (
    <span data-testid={props['data-testid'] || 'sync-status-spinner'} />
  ),
  Tooltip: ({ children, title }: any) => (
    <div data-testid="tooltip" data-title={title}>
      {children}
    </div>
  ),
}));

jest.mock('@mui/icons-material/CheckCircle', () => {
  const MockCheckCircle = (props: any) => (
    <span data-testid={props['data-testid'] || 'sync-status-success'} onClick={props.onClick} />
  );
  MockCheckCircle.displayName = 'MockCheckCircle';
  return { __esModule: true, default: MockCheckCircle };
});

jest.mock('@mui/icons-material/WarningAmber', () => {
  const MockWarningAmber = (props: any) => (
    <span
      data-testid={props['data-testid'] || 'sync-status-failed'}
      onClick={props.onClick}
      role={props.role}
      aria-label={props['aria-label']}
    />
  );
  MockWarningAmber.displayName = 'MockWarningAmber';
  return { __esModule: true, default: MockWarningAmber };
});

jest.mock('@mui/icons-material/InfoOutlined', () => {
  const MockInfoOutlined = (props: any) => (
    <span
      data-testid={props['data-testid'] || 'sync-status-conflict'}
      aria-label={props['aria-label']}
    />
  );
  MockInfoOutlined.displayName = 'MockInfoOutlined';
  return { __esModule: true, default: MockInfoOutlined };
});

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
  useAppSelector: jest.fn(),
}));

// Mock retryOperation
const mockRetryOperation = jest.fn((id: string) => ({
  type: 'syncQueue/retryOperation',
  payload: id,
}));
jest.mock('../../../redux/slices/syncQueue/syncQueueSlice', () => ({
  retryOperation: (id: string) => mockRetryOperation(id),
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

// Mock selectors
const mockSelectOperationByContact = jest.fn();
const mockSelectRetryCount = jest.fn();
const mockSelectMaxRetries = jest.fn();
jest.mock('../../../redux/slices/syncQueue/selectors', () => ({
  selectOperationByContact: (...args: any[]) => mockSelectOperationByContact(...args),
  selectRetryCount: (...args: any[]) => mockSelectRetryCount(...args),
  selectMaxRetries: (...args: any[]) => mockSelectMaxRetries(...args),
}));

// Get reference to mocked useAppSelector
import { useAppSelector } from '../../../types/hooks';
const mockUseAppSelector = useAppSelector as jest.MockedFunction<typeof useAppSelector>;

import SyncStatusIcon from '../SyncStatusIcon';

// ---- Helpers ----

const defaultProps = {
  resourceName: 'people/c12345',
  fieldPath: 'names',
  contactName: 'Alice Smith',
  fieldLabel: 'Name',
};

/**
 * Creates a mock SyncOperation with the given overrides.
 */
const createMockOperation = (overrides: Partial<SyncOperation> = {}): SyncOperation => ({
  id: 'op-123',
  resourceName: 'people/c12345',
  fieldPath: 'names',
  newValue: 'New Name',
  oldValue: 'Old Name',
  status: 'pending',
  timestamp: new Date().toISOString(),
  error: null,
  ...overrides,
});

/**
 * Configure mockUseAppSelector to return specific operations, retry count, and maxRetries.
 * The component calls useAppSelector 3 times per render:
 *   1st: selectOperationByContact → operations array
 *   2nd: selectRetryCount → retry count number
 *   3rd: selectMaxRetries → max retries number
 */
const setupSelector = (operations: SyncOperation[], retryCount: number = 0, maxRetries: number = 5): void => {
  let callCount = 0;
  mockUseAppSelector.mockImplementation(() => {
    callCount += 1;
    const phase = callCount % 3;
    if (phase === 1) {
      return operations;
    }
    if (phase === 2) {
      return retryCount;
    }
    return maxRetries;
  });
};

// ---- Tests ----

describe('SyncStatusIcon', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    jest.useFakeTimers();
  });

  afterEach(() => {
    jest.useRealTimers();
  });

  describe('ReturnsNull_Should_RenderNothing_When_NoOperationExists', () => {
    it('should return null when no operation matches the fieldPath', () => {
      setupSelector([]);

      const { container } = render(<SyncStatusIcon {...defaultProps} />);

      expect(container.innerHTML).toBe('');
      expect(screen.queryByTestId('sync-status-spinner')).not.toBeInTheDocument();
      expect(screen.queryByTestId('sync-status-success')).not.toBeInTheDocument();
      expect(screen.queryByTestId('sync-status-failed')).not.toBeInTheDocument();
    });

    it('should return null when operations exist for different fieldPath', () => {
      const otherOp = createMockOperation({ fieldPath: 'emailAddresses' });
      setupSelector([otherOp]);

      const { container } = render(<SyncStatusIcon {...defaultProps} />);

      expect(container.innerHTML).toBe('');
    });
  });

  describe('Spinner_Should_ShowSpinner_When_StatusIsPending', () => {
    it('should show spinner icon when status is pending', () => {
      const op = createMockOperation({ status: 'pending' });
      setupSelector([op]);

      render(<SyncStatusIcon {...defaultProps} />);

      expect(screen.getByTestId('sync-status-spinner')).toBeInTheDocument();
    });

    it('should show "Syncing..." tooltip when status is pending', () => {
      const op = createMockOperation({ status: 'pending' });
      setupSelector([op]);

      render(<SyncStatusIcon {...defaultProps} />);

      const tooltip = screen.getByTestId('tooltip');
      expect(tooltip).toHaveAttribute('data-title', 'Syncing...');
    });
  });

  describe('Spinner_Should_ShowSpinner_When_StatusIsInProgress', () => {
    it('should show spinner icon when status is in-progress', () => {
      const op = createMockOperation({ status: 'in-progress' });
      setupSelector([op]);

      render(<SyncStatusIcon {...defaultProps} />);

      expect(screen.getByTestId('sync-status-spinner')).toBeInTheDocument();
    });

    it('should show "Syncing..." tooltip when in-progress with no retries', () => {
      const op = createMockOperation({ status: 'in-progress' });
      setupSelector([op], 0);

      render(<SyncStatusIcon {...defaultProps} />);

      const tooltip = screen.getByTestId('tooltip');
      expect(tooltip).toHaveAttribute('data-title', 'Syncing...');
    });

    it('should show retry attempt tooltip when in-progress with retryCount > 0', () => {
      const op = createMockOperation({ status: 'in-progress' });
      setupSelector([op], 2);

      render(<SyncStatusIcon {...defaultProps} />);

      const tooltip = screen.getByTestId('tooltip');
      expect(tooltip).toHaveAttribute('data-title', 'Retrying... (Attempt 2 of 5)');
    });
  });

  describe('Checkmark_Should_ShowCheckmark_When_StatusIsSuccess', () => {
    it('should show checkmark icon when status is success', () => {
      const op = createMockOperation({ status: 'success' });
      setupSelector([op]);

      render(<SyncStatusIcon {...defaultProps} />);

      expect(screen.getByTestId('sync-status-success')).toBeInTheDocument();
    });

    it('should show "Synced" tooltip when status is success', () => {
      const op = createMockOperation({ status: 'success' });
      setupSelector([op]);

      render(<SyncStatusIcon {...defaultProps} />);

      const tooltip = screen.getByTestId('tooltip');
      expect(tooltip).toHaveAttribute('data-title', 'Synced');
    });
  });

  describe('AutoHide_Should_HideAfterTwoSeconds_When_Success', () => {
    it('should hide the icon after 2 seconds when status is success', () => {
      const op = createMockOperation({ status: 'success' });
      setupSelector([op]);

      render(<SyncStatusIcon {...defaultProps} />);

      expect(screen.getByTestId('sync-status-success')).toBeInTheDocument();

      act(() => {
        jest.advanceTimersByTime(2000);
      });

      expect(screen.queryByTestId('sync-status-success')).not.toBeInTheDocument();
    });

    it('should not hide before 2 seconds', () => {
      const op = createMockOperation({ status: 'success' });
      setupSelector([op]);

      render(<SyncStatusIcon {...defaultProps} />);

      act(() => {
        jest.advanceTimersByTime(1500);
      });

      expect(screen.getByTestId('sync-status-success')).toBeInTheDocument();
    });
  });

  describe('WarningTriangle_Should_ShowWarning_When_StatusIsFailed', () => {
    it('should show warning icon when status is failed', () => {
      const op = createMockOperation({ status: 'failed', error: 'Network timeout' });
      setupSelector([op], 1);

      render(<SyncStatusIcon {...defaultProps} />);

      expect(screen.getByTestId('sync-status-failed')).toBeInTheDocument();
    });

    it('should show error tooltip when failed with retries remaining', () => {
      const op = createMockOperation({ status: 'failed', error: 'Network timeout' });
      setupSelector([op], 1);

      render(<SyncStatusIcon {...defaultProps} />);

      const tooltip = screen.getByTestId('tooltip');
      expect(tooltip).toHaveAttribute('data-title', 'Sync failed: Network timeout');
    });

    it('should show "click to retry" tooltip when max retries exceeded', () => {
      const op = createMockOperation({ status: 'failed', error: 'Server error' });
      setupSelector([op], 5);

      render(<SyncStatusIcon {...defaultProps} />);

      const tooltip = screen.getByTestId('tooltip');
      expect(tooltip).toHaveAttribute('data-title', 'Sync failed. Click to retry manually.');
    });

    it('should not auto-hide when status is failed', () => {
      const op = createMockOperation({ status: 'failed', error: 'Error' });
      setupSelector([op], 1);

      render(<SyncStatusIcon {...defaultProps} />);

      act(() => {
        jest.advanceTimersByTime(5000);
      });

      expect(screen.getByTestId('sync-status-failed')).toBeInTheDocument();
    });
  });

  describe('ManualRetry_Should_DispatchRetryAndExecute_When_FailedIconClicked', () => {
    it('should dispatch retryOperation and executeFieldUpdate on click', () => {
      const op = createMockOperation({
        status: 'failed',
        error: 'API error',
        newValue: 'New Name',
        oldValue: 'Old Name',
      });
      setupSelector([op], 5);

      render(<SyncStatusIcon {...defaultProps} />);

      const warningIcon = screen.getByTestId('sync-status-failed');
      fireEvent.click(warningIcon);

      expect(mockDispatch).toHaveBeenCalledTimes(2);
      expect(mockRetryOperation).toHaveBeenCalledWith('op-123');
      expect(mockExecuteFieldUpdate).toHaveBeenCalledWith({
        resourceName: 'people/c12345',
        fieldPath: 'names',
        newValue: 'New Name',
        oldValue: 'Old Name',
      });
    });
  });

  describe('AriaLiveRegion_Should_AnnounceStatus_When_StatusChanges', () => {
    it('should announce success message in ARIA live region', () => {
      const op = createMockOperation({ status: 'success' });
      setupSelector([op]);

      render(<SyncStatusIcon {...defaultProps} />);

      expect(screen.getByText('Alice Smith Name synced successfully')).toBeInTheDocument();
    });

    it('should announce failure message in ARIA live region', () => {
      const op = createMockOperation({ status: 'failed', error: 'Network error' });
      setupSelector([op], 1);

      render(<SyncStatusIcon {...defaultProps} />);

      expect(screen.getByText('Alice Smith Name sync failed')).toBeInTheDocument();
    });

    it('should have aria-live="polite" and aria-atomic="true" attributes', () => {
      const op = createMockOperation({ status: 'failed', error: 'Error' });
      setupSelector([op], 1);

      render(<SyncStatusIcon {...defaultProps} />);

      const ariaText = screen.getByText('Alice Smith Name sync failed');
      // Walk up to find the element with aria-live (mock Box renders as div with spread props)
      let liveRegion: HTMLElement | null = ariaText;
      while (liveRegion && !liveRegion.getAttribute('aria-live')) {
        liveRegion = liveRegion.parentElement;
      }
      expect(liveRegion).not.toBeNull();
      expect(liveRegion).toHaveAttribute('aria-live', 'polite');
      expect(liveRegion).toHaveAttribute('aria-atomic', 'true');
    });
  });

  describe('InferredSuccess_Should_ShowCheckmark_When_OperationRemoved', () => {
    it('should show checkmark when a previously in-progress operation disappears', () => {
      const op = createMockOperation({ status: 'in-progress' });
      setupSelector([op]);

      const { rerender } = render(<SyncStatusIcon {...defaultProps} />);

      expect(screen.getByTestId('sync-status-spinner')).toBeInTheDocument();

      // Operation disappears (success removes it from Redux)
      setupSelector([]);
      rerender(<SyncStatusIcon {...defaultProps} />);

      expect(screen.getByTestId('sync-status-success')).toBeInTheDocument();
    });

    it('should auto-hide inferred success after 2 seconds', () => {
      const op = createMockOperation({ status: 'in-progress' });
      setupSelector([op]);

      const { rerender } = render(<SyncStatusIcon {...defaultProps} />);

      setupSelector([]);
      rerender(<SyncStatusIcon {...defaultProps} />);

      expect(screen.getByTestId('sync-status-success')).toBeInTheDocument();

      act(() => {
        jest.advanceTimersByTime(2000);
      });

      expect(screen.queryByTestId('sync-status-success')).not.toBeInTheDocument();
    });

    it('should announce success in ARIA region when inferred', () => {
      const op = createMockOperation({ status: 'in-progress' });
      setupSelector([op]);

      const { rerender } = render(<SyncStatusIcon {...defaultProps} />);

      setupSelector([]);
      rerender(<SyncStatusIcon {...defaultProps} />);

      expect(screen.getByText('Alice Smith Name synced successfully')).toBeInTheDocument();
    });
  });

  describe('MultipleOperations_Should_FilterByFieldPath_When_Rendered', () => {
    it('should only display status for matching fieldPath operation', () => {
      const nameOp = createMockOperation({
        id: 'op-1',
        fieldPath: 'names',
        status: 'failed',
        error: 'Error',
      });
      const emailOp = createMockOperation({
        id: 'op-2',
        fieldPath: 'emailAddresses',
        status: 'pending',
      });
      setupSelector([nameOp, emailOp], 1);

      render(<SyncStatusIcon {...defaultProps} />);

      // Should show the failed icon for 'names', not the spinner for 'emailAddresses'
      expect(screen.getByTestId('sync-status-failed')).toBeInTheDocument();
      expect(screen.queryByTestId('sync-status-spinner')).not.toBeInTheDocument();
    });
  });
});
