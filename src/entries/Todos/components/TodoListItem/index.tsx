import React from 'react';
import { IconButton, Paper, Typography } from '@mui/material';
import { Delete } from '@mui/icons-material';
import { styled } from '@mui/material/styles';

const Root = styled(Paper)(({ theme }) => ({
  padding: theme.spacing(2),
  display: 'flex',
  margin: theme.spacing(2),
  alignItems: 'center',
  justifyContent: 'space-between',
  border: `1px solid ${theme.palette.divider}`,
  borderRadius: theme.shape.borderRadius,
  cursor: 'pointer',
}));

export type TodoListItemProps = {
  /**
   * The label of the todo item.
   */
  label: string;

  /**
   * The function to call when the todo item is clicked.
   *
   * @returns void
   *
   */
  onClick?: () => void;

  /**
   * The function to call when the delete button is clicked.
   */
  onDeleteClick?: () => void;
};

const TodoListItem = ({ label, onClick, onDeleteClick }: TodoListItemProps) => {
  return (
    <Root elevation={3} onClick={onClick}>
      <Typography>{label}</Typography>
      <IconButton
        onClick={(e) => {
          e.stopPropagation();
          onDeleteClick?.();
        }}
      >
        <Delete />
      </IconButton>
    </Root>
  );
};

export default TodoListItem;
