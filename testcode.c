struct Point {
    int x;
    int y;
};

struct Point *next_ptr(struct Point *p) {
    return p + 1;
}

struct Point *head(struct Point *p) {
    return p;
}


struct Point g_p;
struct Point *foo() {
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


int main() {
    struct Point pts[2];
    struct Point *p;
    

    p = pts;
    pts[0].x = 10;
    pts[0].y = 20;
    pts[1].x = 30;
    pts[1].y = 40;

    printf("%d %d\n", (p + 1)->x, (p + 1)->y);
    printf("%d %d\n", foo()->x, foo()->y);   // foo が struct Point* を返すなら可
    printf("%d\n", next_ptr(p)->x);
    printf("%d\n", head(p)->y);

    counter();

    return 0;
}
