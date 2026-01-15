import React from 'react';
import { Meta, StoryObj } from '@storybook/react';
import TodoListItem, { TodoListItemProps } from '../';

const meta: Meta<typeof TodoListItem> = {
  title: 'COMPONENTS/TodoListItem',
  component: TodoListItem,
  tags: ['autodocs'],
  decorators: [
    (Story) => (
      <div style={{ width: '300px' }}>
        <Story />
      </div>
    ),
  ],
};

const dummyProps: TodoListItemProps = {
  label: 'Todo 1',
};

type Story = StoryObj<typeof TodoListItem>;

export const Default: Story = { args: dummyProps };

export const LongText: Story = {
  args: {
    ...dummyProps,
    label: 'This is a very long label that should be truncated or wrapped in the component',
  },
};

export const ShortText: Story = {
  args: {
    ...dummyProps,
    label: 'a',
  },
};

export default meta;
