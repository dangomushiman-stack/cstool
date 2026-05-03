typedef struct Point_st {
    int x;
    int y;
}Point;

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

    counter();

    return 0;
}
