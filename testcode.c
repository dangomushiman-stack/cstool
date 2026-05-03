typedef struct Point_st {
    int x;
    int y;
}Point;

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
    b = &c;
    a = &b;
    printf("**a = %d\n",**a);
    

    p = pts;
    pts[0].x = 10;
    pts[0].y = 20;
    pts[1].x = 30;
    pts[1].y = 40;

    printf("%d %d\n", (p + 1)->x, (p + 1)->y);
    printf("%d %d\n", foo()->x, foo()->y);   // foo が struct Point* を返すなら可
    printf("%d\n", next_ptr(p)->x);
    printf("%d\n", head(p)->y);
    printf("field array: %d %d %s %d %d\n", fa.values[0], fa.values[2], fa.name, fa.points[1].x, fa.points[1].y);
    printf("sizeof: %d %d %d %d %d %d %d\n", sizeof(int), sizeof(Point), sizeof(pts), sizeof(pts[0]), sizeof(p), sizeof(fa), sizeof(fa.values));
    printf("sizeof side effect: %d %d\n", sizeof(z++), z);
    printf("hex pointer: %x %p %p\n", c, &c, p);
    printf("hex literal: %d %x\n", 0x10, 0xff);
    printf("bitwise: %x %x %x %x %x %x\n", 0xf0 & 0x0f, 0xf0 | 0x0f, 0xf0 ^ 0xff, ~0x0f, 0x10 << 1, 0x10 >> 2);
    printf("multi decl: %d %d %d %d %c %s %d %d %d %d %d %d\n", multi_a, multi_b, multi_arr[0], multi_arr[1], multi_c, multi_s, multi_p1.x, multi_p2.y, multi_mode, multi_mode2, *multi_ptr, multi_plain);
    printf("global multi: %d %d\n", g_multi_a, g_multi_b);

    char escstr[5] = "A\\\"B";
    printf("escapes: %d %d %d %d %s\n", '\n', '\t', '\\', '\'', escstr);

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
