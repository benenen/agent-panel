#pragma once
#include <stdint.h>
#include <stddef.h>
typedef struct ap_terminal ap_terminal;
/* Fixed ABI isolates .NET from the evolving libghostty-vt structs. */
typedef struct { uint32_t foreground, background, flags; char text[64]; } ap_cell;
ap_terminal *ap_create(const char *directory, const char *command, int cols, int rows);
int ap_pump(ap_terminal *terminal);
int ap_write(ap_terminal *terminal, const uint8_t *bytes, size_t length);
int ap_key(ap_terminal *terminal, int key, int modifiers);
int ap_paste(ap_terminal *terminal, uint8_t *bytes, size_t length);
int ap_resize(ap_terminal *terminal, int cols, int rows);
int ap_snapshot(ap_terminal *terminal, ap_cell *cells, int capacity, int *cursor_x, int *cursor_y);
void ap_scroll(ap_terminal *terminal, int lines);
void ap_destroy(ap_terminal *terminal);
