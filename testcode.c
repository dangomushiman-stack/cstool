#include "predefs.h"

typedef struct Point_st {
    int x;
    int y;
}Point;

#define PRE_BASE 7
#define PRE_ADD(a, b) ((a) + (b))
#define PRE_ENABLED

enum Mode {
    MODE_ZERO,
    MODE_ONE = 3,
    MODE_TWO,
    MODE_MASK = 1 << 4
};

typedef enum {
    SMALL = 2,
    LARGE = SMALL + 5
} SizeKind;

int proto_add(int, int);

void void_return_test(int *value)
{
    *value += 1;
    return;
    *value += 100;
}

/*
 * Block comments are ignored by the lexer.
 * They can span multiple lines.
 */
Point *next_ptr( Point *p) {
    return p + 1;
}

Point *head(Point *p) {
    return p;
}

void reverse_string(char *str) {
    if (str == NULL) return;

    char *end = str;
    char temp;

    while (*end != '\0') {
        end++;
    }
    end--;

    while (str < end) {
        temp = *str;
        *str = *end;
        *end = temp;

        str++;
        end--;
    }
}

int my_strlen(char *s) {
    char *p = s;
    while (*p != '\0') {
        p++;
    }

    return p - s;
}

void my_strcpy(char *dst, char *src) {
    while (*src != '\0') {
        *dst = *src;
        dst++;
        src++;
    }

    *dst = '\0';
}

int sum_array_param(int values[3]) {
    return values[0] + values[1] + values[2];
}

void bump_array_param(int values[]) {
    values[1] += 10;
}

char first_char_param(char text[]) {
    return text[0];
}

int argv_name_score(char *argv[]) {
    return my_strlen(argv[0]) + my_strlen(argv[1]) + argv[2][0];
}

int argv_double_score(char **argv) {
    return my_strlen(argv[1]) + argv[2][1];
}

int sum_matrix_param(int values[][3]) {
    return values[0][0] + values[0][1] + values[0][2] + values[1][0] + values[1][1] + values[1][2];
}

int bump_matrix_param(int values[][3]) {
    values[0][2] += values[1][1];
    return values[0][2] + values[1][2];
}

int sum_cube_param(int values[][3][4]) {
    return values[0][0][0] + values[1][2][3];
}

int bump_cube_param(int values[][3][4]) {
    values[1][1][2] += 100;
    return values[1][1][2];
}


Point g_p;
int g_multi_a = 1, g_multi_b = 2;
Point *foo() {
    g_p.x=100;
    return &g_p;
}


int count;

int counter()
{
    count++;
    if(count<10)
    {
        printf("count=%d\n",count);
        counter();
    }
    return 0;
}

struct FieldArrayTest {
    int values[3];
    char name[4];
    Point points[2];
};


int main() {
    Point pts[2];
    Point *p;
    struct FieldArrayTest fa = { {7, 8, 9}, "abc", {{1, 2}, {3, 4}} };
    int **a;
    int  *b;
    int  c=50;
    int  z=1;
    int  control=0;
    int multi_a=1, multi_b=2, multi_arr[2] = {3, 4};
    char multi_c='Z', multi_s[]="xy";
    Point multi_p1 = {5, 6}, multi_p2 = {7, 8};
    enum Mode multi_mode=MODE_ONE, multi_mode2=MODE_TWO;
    int *multi_ptr=&c, multi_plain=6;
    int pre_value = PRE_ADD(PRE_BASE, 5);
    int include_value = INCLUDED_ADD(INCLUDED_BASE, 2);
#ifdef PRE_ENABLED
    pre_value += 1;
#else
    pre_value += 100;
#endif
    b = &c;
    a = &b;
    printf("**a = %d\n",**a);
    printf("preprocessor: %d %d\n", pre_value, include_value);
    

    p = pts;
    pts[0].x = 10;
    pts[0].y = 20;
    pts[1].x = 30;
    pts[1].y = 40;

    printf("%d %d\n", (p + 1)->x, (p + 1)->y);
    printf("%d %d\n", foo()->x, foo()->y);   // foo が struct Point* を返すなら可
    printf("%d\n", next_ptr(p)->x);
    printf("%d\n", head(p)->y);
    int pointer_nums[3] = {11, 22, 33};
    int *ip = pointer_nums;
    int postfix_old = *ip++;
    int postfix_now = *ip;
    ++ip;
    int prefix_now = *ip;
    ip--;
    int back_now = *ip;
    p = pts;
    int struct_before = p->x;
    p++;
    int struct_after = p->x;
    --p;
    int struct_back = p->y;
    printf("pointer incdec: %d %d %d %d %d %d %d\n", postfix_old, postfix_now, prefix_now, back_now, struct_before, struct_after, struct_back);
    int *ip_first = pointer_nums;
    int *ip_last = &pointer_nums[2];
    p = pts;
    Point *p_first = p;
    Point *p_last = p + 1;
    printf("pointer diff: %d %d\n", ip_last - ip_first, p_last - p_first);
    printf("field array: %d %d %s %d %d\n", fa.values[0], fa.values[2], fa.name, fa.points[1].x, fa.points[1].y);
    printf("sizeof: %d %d %d %d %d %d %d\n", sizeof(int), sizeof(Point), sizeof(pts), sizeof(pts[0]), sizeof(p), sizeof(fa), sizeof(fa.values));
    printf("sizeof side effect: %d %d\n", sizeof(z++), z);
    printf("hex pointer: %x %p %p\n", c, &c, p);
    printf("hex literal: %d %x\n", 0x10, 0xff);
    printf("bitwise: %x %x %x %x %x %x\n", 0xf0 & 0x0f, 0xf0 | 0x0f, 0xf0 ^ 0xff, ~0x0f, 0x10 << 1, 0x10 >> 2);
    printf("multi decl: %d %d %d %d %c %s %d %d %d %d %d %d\n", multi_a, multi_b, multi_arr[0], multi_arr[1], multi_c, multi_s, multi_p1.x, multi_p2.y, multi_mode, multi_mode2, *multi_ptr, multi_plain);
    printf("global multi: %d %d\n", g_multi_a, g_multi_b);

    char message[] = "Pointer Magic";
    printf("reverse before: %s\n", message);
    reverse_string(message);
    printf("reverse after: %s\n", message);
    char copy_src[] = "copy me";
    char copy_dst[16];
    my_strcpy(copy_dst, copy_src);
    printf("string funcs: %d %s\n", my_strlen(copy_dst), copy_dst);
    int array_param_values[3] = { 4, 5, 6 };
    bump_array_param(array_param_values);
    printf("array params: %d %d %c\n", sum_array_param(array_param_values), array_param_values[1], first_char_param(copy_dst));
    int matrix[2][3] = {{1, 2, 3}, {4, 5, 6}};
    matrix[1][2] = 20;
    matrix[0][1] += matrix[1][0];
    int *matrix_row = matrix[1];
    printf("multi array: %d %d %d %d %d %d %d\n", matrix[0][1], matrix[1][2], matrix_row[2], sizeof(matrix), sizeof(matrix[0]), sum_matrix_param(matrix), bump_matrix_param(matrix));
    int (*matrix_ptr)[3] = matrix;
    int ptr_before = matrix_ptr[1][2];
    int ptr_first = (*matrix_ptr)[0];
    matrix_ptr++;
    int ptr_after = (*matrix_ptr)[0];
    (*matrix_ptr)[1] += 30;
    printf("array pointer: %d %d %d %d\n", ptr_before, ptr_first, ptr_after, matrix[1][1]);

    int cube[2][3][4] = {
        {{1, 2, 3, 4}, {5, 6, 7, 8}, {9, 10, 11, 12}},
        {{13, 14, 15, 16}, {17, 18, 19, 20}, {21, 22, 23, 24}}
    };
    cube[1][2][3] = 40;
    cube[0][1][2] += cube[1][0][0];
    int cube_sum = sum_cube_param(cube);
    int cube_bump = bump_cube_param(cube);
    int (*cube_ptr)[3][4] = cube;
    int cube_ptr_before = cube_ptr[1][1][2];
    cube_ptr++;
    int cube_ptr_after = (*cube_ptr)[2][3];
    printf("cube array: %d %d %d %d %d %d %d %d %d\n", cube[0][1][2], cube[1][2][3], sizeof(cube), sizeof(cube[0]), sizeof(cube[0][0]), cube_sum, cube_bump, cube_ptr_before, cube_ptr_after);

    char *argv_names[] = {"alpha", "beta", "gamma"};
    char *argv_more[3];
    argv_more[0] = "red";
    argv_more[1] = "green";
    argv_more[2] = argv_names[2];
    char **argv_pp = argv_names;
    argv_pp++;
    printf("argv pointer: %s %c %d %d %s\n", argv_names[1], argv_names[2][1], argv_name_score(argv_names), argv_double_score(argv_names), argv_pp[1]);

    void *void_ptr;
    int void_int = 1234;
    char void_char = 'A';
    void_ptr = &void_int;
    int void_read_int = *(int *)void_ptr;
    *(int *)void_ptr = 5678;
    void_ptr = &void_char;
    char void_read_char = *(char *)void_ptr;
    *(char *)void_ptr = 'Z';
    void_ptr = pts;
    printf("void pointer: %d %d %c %c %d\n", void_read_int, void_int, void_read_char, void_char, ((Point *)void_ptr)->y);

    char escstr[5] = "A\\\"B";
    printf("escapes: %d %d %d %d %s\n", '\n', '\t', '\\', '\'', escstr);

    char mem_src[8] = "ABC";
    char mem_dst[8];
    memset(mem_dst, 0, sizeof(mem_dst));
    memcpy(mem_dst, mem_src, 4);
    memset(mem_dst + 1, 'x', 2);
    int mem_values[3];
    memset(mem_values, 0, sizeof(mem_values));
    mem_values[0] = 11;
    mem_values[1] = 22;
    memcpy(mem_values + 2, mem_values, sizeof(int));
    printf("memory funcs: %s %d %d %d\n", mem_dst, mem_values[0], mem_values[1], mem_values[2]);

    int compound=10;
    compound += 5;
    compound -= 3;
    compound *= 2;
    compound /= 4;
    compound %= 5;
    compound |= 0x10;
    compound &= 0x1f;
    compound ^= 0x0f;
    compound <<= 2;
    compound >>= 1;
    p = pts;
    p += 1;
    printf("compound assign: %x %d\n", compound, p->x);

    int logical=0;
    int logical_result1 = 0 && logical++;
    int logical_result2 = 1 || logical++;
    int logical_result3 = 1 && ++logical;
    int logical_result4 = 0 || ++logical;
    printf("logical short circuit: %d %d %d %d %d\n", logical, logical_result1, logical_result2, logical_result3, logical_result4);

    int ternary=0;
    int ternary_result1 = 1 ? ++ternary : ++ternary;
    int ternary_result2 = 0 ? ++ternary : ++ternary;
    int ternary_result3 = c == 50 ? 100 : 200;
    int ternary_result4 = 0 ? 10 : 1 ? 20 : 30;
    printf("ternary: %d %d %d %d %d\n", ternary, ternary_result1, ternary_result2, ternary_result3, ternary_result4);

    enum Mode mode = MODE_TWO;
    SizeKind sizeKind = LARGE;
    int enumArray[SMALL];
    enumArray[0] = MODE_ONE;
    enumArray[1] = MODE_MASK;
    printf("enum: %d %d %d %d %d %d\n", MODE_ZERO, mode, sizeKind, enumArray[0], enumArray[1], sizeof(enum Mode));
    printf("prototype: %d\n", proto_add(MODE_ONE, LARGE));
    int void_ret=0;
    void_return_test(&void_ret);
    printf("void return: %d\n", void_ret);

    int sw=0;
    switch (2) {
    case 1:
        sw += 100;
        break;
    case 2:
        sw += 2;
    case 3:
        sw += 3;
        break;
    default:
        sw += 1000;
    }

    switch (9) {
    case 1:
        sw += 100;
        break;
    default:
        sw += 9;
        break;
    }

    switch ('\n') {
    case '\n':
        sw += 10;
        break;
    default:
        sw += 1000;
    }

    printf("switch: %d\n", sw);

    if (c != 50) {
        control += 100;
    } else {
        control += 1;
    }

    for (int i = 0; i < 5; i++) {
        if (i == 1) {
            continue;
        }
        if (i == 4) {
            break;
        }
        control += i;
    }

    while (control < 8) {
        control++;
    }

    do {
        control++;
    } while (control < 9);

    printf("control flow: %d\n", control);

    counter();

    return 0;
}

int proto_add(int a, int b)
{
    return a + b;
}
