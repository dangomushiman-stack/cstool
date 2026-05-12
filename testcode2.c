typedef int DBNo;

typedef struct VArrayRange_St
{
    int start;
    int length;
}VArrayRange;

int VArrayRange_getEnd(VArrayRange va)
{
    return va.start + va.length - 1;
}


typedef struct Brunch_St
{
    char valid_flag;
    int connected_node[2];

}Brunch;


typedef struct Node_St
{
    char valid_flag;
}Node;

/* DB定義 */
Node NodeDB[100];
Brunch BrunchDB[100];

DBNo NodeDBVArray[100];
DBNo BrunchDBVArray[100];

/* Node関数 */
void Node_init(Node *node)
{
    node->valid_flag = 1;
}

void NodeDB_generate()
{
    int i=0;
    for(i=0;i<100;i++)
    {
        Node_init(&NodeDB[i]);
    }

}

/* Branch関数 */
/* Branchの生成 */
void Brunch_init(Brunch *brunch,DBNo from_no,DBNo to_no)
{
    brunch->valid_flag = 1;
    brunch->connected_node[0] = from_no;
    brunch->connected_node[1] = to_no;

}

/* BranchDBの生成 */
void BrunchDB_generate()
{
    int i=0;
    for(i=0;i<100;i++)
    {

        /* i番目のブランチを使って、ノードiからノード(i+1)%100への接続を作成（リング状のグラフ） */
        Brunch_init(&BrunchDB[i], i, (i + 1) % 100);

    }
}


int main()
{

    NodeDB_generate();
    BrunchDB_generate();

    return 0;

}



