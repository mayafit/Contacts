import React from 'react';
import {
  fireEvent,
  render,
  screen,
  waitFor,
  // waitForElementToBeRemoved,
} from '@testing-library/react'; // import ToggleButtons from '../ToggleButtons';
import '@testing-library/jest-dom';

import ExternalAction from '../ExternalAction';
// import dummyProps from '../__mock__/ToggleButtons.mock';
// import '@testing-library/jest-dom/extend-expect';

describe('<ExternalAction/>', () => {
  it('ExternalAction should render without props except text', async () => {
    render(<ExternalAction />);
    // expect(true).toBeTruthy();
    // return;
    // const button = screen.getByTestId('btn1');
    const button = screen.getByRole('button');

    const tooltip = screen.queryByText(/i'm a tooltip/i);
    expect(tooltip).not.toBeInTheDocument();

    fireEvent.mouseOver(button);
    // await waitFor(() => expect(screen.queryByText(/i'm a tooltip/i)).toBeInTheDocument());
    await waitFor(() => expect(screen.queryByText(/i'm a tooltip/i)).toBeInTheDocument());

    fireEvent.mouseOut(button);
    await waitFor(() => expect(screen.queryByText(/i'm a tooltip/i)).not.toBeInTheDocument());
  });
});
